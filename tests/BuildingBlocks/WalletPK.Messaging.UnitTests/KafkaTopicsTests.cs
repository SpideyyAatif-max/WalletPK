using System.Text.RegularExpressions;

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
