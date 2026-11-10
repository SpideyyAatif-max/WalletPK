using System.Text.RegularExpressions;
using WalletPK.Messaging.Kafka;

namespace WalletPK.Messaging.UnitTests;

public class KafkaTopicsTests
{
    [Fact]
    public void Topic_constants_match_the_topics_docker_creates()
    {
        var script = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "ops", "kafka", "create-topics.sh"));

        var createdTopics = Regex.Matches(script, @"^create\s+(\S+)", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            .ToArray();

        createdTopics.ShouldBe(KafkaTopics.All, ignoreOrder: true);
    }

    [Fact]
    public void Topics_created_by_the_services_have_the_same_partitions_as_those_docker_creates()
    {
        var script = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "ops", "kafka", "create-topics.sh"));

        var partitions = Regex.Matches(script, @"^create\s+(\S+)\s+(\d+)", RegexOptions.Multiline)
            .ToDictionary(match => match.Groups[1].Value, match => int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));

        KafkaTopicAdmin.Partitions.ShouldBe(partitions, ignoreOrder: true);
    }

    [Fact]
    public void Topic_names_are_unique()
    {
        KafkaTopics.All.Distinct().Count().ShouldBe(KafkaTopics.All.Count);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WalletPK.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not find the repository root (WalletPK.sln).");
    }
}
