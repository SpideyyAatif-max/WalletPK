using System.Security.Cryptography;
using System.Text.Json;
using WalletPK.Common.Identifiers;
using WalletPK.Identity.Application.Events;
using WalletPK.Identity.Domain.Devices;
using WalletPK.Identity.Domain.Users;
using WalletPK.Identity.Infrastructure.Security;
using WalletPK.Messaging;
using WalletPK.Messaging.Serialization;
using IdentityContracts = WalletPK.Contracts.Identity;

namespace WalletPK.Identity.UnitTests.Infrastructure;

public class EncryptionAndEventTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);

    private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static EncryptionOptions Options(params (string Id, string Key)[] keys) => new()
    {
        Keys = keys.ToDictionary(key => key.Id, key => key.Key, StringComparer.Ordinal),
        ActiveKeyId = keys[^1].Id,
        BlindIndexKey = NewKey(),
    };

    // ---------------- AES-256-GCM ----------------

    [Fact]
    public void Encryption_round_trips_and_never_repeats_a_ciphertext()
    {
        var encryptor = new AesGcmFieldEncryptor(Options(("k1", NewKey())));

        var first = encryptor.Encrypt("3520212345671", "user-1");
        var second = encryptor.Encrypt("3520212345671", "user-1");

        first.ShouldNotBe(second); // random nonce per value
        first.Split('.').Length.ShouldBe(4);
        encryptor.Decrypt(first, "user-1").ShouldBe("3520212345671");
        encryptor.Decrypt(second, "user-1").ShouldBe("3520212345671");
    }

    [Fact]
    public void A_ciphertext_moved_to_another_user_or_tampered_with_does_not_decrypt()
    {
        var encryptor = new AesGcmFieldEncryptor(Options(("k1", NewKey())));
        var ciphertext = encryptor.Encrypt("3520212345671", "user-1");
        var parts = ciphertext.Split('.');
        var flipped = parts[3][0] == 'A' ? 'B' + parts[3][1..] : 'A' + parts[3][1..];
        var tampered = string.Join('.', parts[0], parts[1], parts[2], flipped);

        Should.Throw<CryptographicException>(() => encryptor.Decrypt(ciphertext, "user-2"));
        Should.Throw<CryptographicException>(() => encryptor.Decrypt(tampered, "user-1"));
        Should.Throw<CryptographicException>(() => encryptor.Decrypt("v1.unknown.AAAA.AAAA", "user-1"));
        Should.Throw<CryptographicException>(() => encryptor.Decrypt("not-a-ciphertext", "user-1"));
    }

    [Fact]
    public void After_key_rotation_old_values_still_decrypt_and_new_values_use_the_new_key()
    {
        var oldKey = NewKey();
        var before = new AesGcmFieldEncryptor(Options(("k1", oldKey)));
        var oldCiphertext = before.Encrypt("3520212345671", "user-1");

        var after = new AesGcmFieldEncryptor(Options(("k1", oldKey), ("k2", NewKey())));

        after.Decrypt(oldCiphertext, "user-1").ShouldBe("3520212345671");
        after.Encrypt("3520212345671", "user-1").ShouldStartWith("v1.k2.");
    }

    [Fact]
    public void Invalid_key_configuration_is_rejected()
    {
        new EncryptionOptions().HasValidKeys().ShouldBeFalse();
        Options(("k1", Convert.ToBase64String(new byte[16]))).HasValidKeys().ShouldBeFalse(); // AES-128 key: too short
        Options(("bad.id", NewKey())).HasValidKeys().ShouldBeFalse();
        new EncryptionOptions { BlindIndexKey = "not base64!" }.HasValidBlindIndexKey().ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => new AesGcmFieldEncryptor(new EncryptionOptions()));
    }

    [Fact]
    public void The_blind_index_is_stable_per_key_and_reveals_nothing()
    {
        var options = Options(("k1", NewKey()));
        var index = new HmacBlindIndex(options);
        var otherKey = new HmacBlindIndex(Options(("k1", NewKey())));

        index.Compute("3520212345671").ShouldBe(index.Compute("3520212345671"));
        index.Compute("3520212345671").ShouldNotBe(index.Compute("3520212345672"));
        index.Compute("3520212345671").ShouldNotBe(otherKey.Compute("3520212345671"));
        index.Compute("3520212345671").Length.ShouldBe(64);
    }

    // ---------------- Integration events ----------------

    [Fact]
    public void Public_domain_events_become_identity_integration_events_keyed_by_user()
    {
        var userId = UserId.New();
        var deviceId = DeviceId.New();

        var registered = IdentityIntegrationEvents.FromDomainEvent(new UserRegistered(userId, "+923001234567", _now));
        var bound = IdentityIntegrationEvents.FromDomainEvent(new DeviceTrusted(deviceId, userId, _now, _now.AddHours(24)));
        var upgraded = IdentityIntegrationEvents.FromDomainEvent(new KycUpgraded(userId, KycTier.L0, KycTier.L1, _now));
        var lockedOut = IdentityIntegrationEvents.FromDomainEvent(new UserLockedOut(userId, _now, _now.AddMinutes(30)));

        var user = registered.ShouldBeOfType<IdentityContracts.UserRegistered>();
        user.PhoneNumber.ShouldBe("+923001234567");
        user.KycTier.ShouldBe("L0");
        user.GetPartitionKey().ShouldBe(userId.ToString());
        bound.ShouldBeOfType<IdentityContracts.DeviceBound>().CoolingOffUntil.ShouldBe(_now.AddHours(24));
        upgraded.ShouldBeOfType<IdentityContracts.KycUpgraded>().NewTier.ShouldBe("L1");
        lockedOut.ShouldBeNull(); // internal only
    }

    [Fact]
    public void The_integration_event_keeps_the_domain_event_id_and_time()
    {
        var domainEvent = new UserRegistered(UserId.New(), "+923001234567", _now);

        var integrationEvent = IdentityIntegrationEvents.FromDomainEvent(domainEvent).ShouldNotBeNull();

        integrationEvent.EventId.ShouldBe(domainEvent.EventId);
        integrationEvent.OccurredAt.ShouldBe(_now);
    }

    [Fact]
    public void Every_identity_contract_registers_and_serialises_to_an_envelope()
    {
        var registry = new IntegrationEventRegistry()
            .RegisterFromAssembly(typeof(IdentityContracts.UserRegistered).Assembly, IdentityIntegrationEvents.ContractsNamespace);
        var serializer = new JsonEventSerializer(registry);
        var userId = UserId.New();

        var envelope = serializer.Wrap(
            IdentityIntegrationEvents.FromDomainEvent(new KycUpgraded(userId, KycTier.L0, KycTier.L1, _now))!,
            correlationId: "corr-1");

        registry.EventTypes.Count.ShouldBe(3);
        envelope.EventType.ShouldBe("identity.kyc_upgraded");
        envelope.Topic.ShouldBe(KafkaTopics.IdentityEvents);
        envelope.PartitionKey.ShouldBe(userId.ToString());
        envelope.CorrelationId.ShouldBe("corr-1");
        using var document = JsonDocument.Parse(envelope.Payload);
        var payload = document.RootElement;
        payload.GetProperty("userId").GetGuid().ShouldBe(userId.Value);
        payload.GetProperty("newTier").GetString().ShouldBe("L1");

        serializer.TryUnwrap(envelope, out var restored).ShouldBeTrue();
        restored.ShouldBeOfType<IdentityContracts.KycUpgraded>().UserId.ShouldBe(userId.Value);
    }
}
