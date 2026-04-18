using Ado.AutonomousWorker.Cli.Interactive;

namespace Ado.AutonomousWorker.Tests;

public sealed class SlashCommandRegistryTests
{
    private readonly SlashCommandRegistry _registry = new();

    [Test]
    public async Task GetAll_ReturnsAllRegisteredCommands()
    {
        var all = _registry.GetAll();

        await Assert.That(all.Count).IsGreaterThanOrEqualTo(20);
    }

    [Test]
    public async Task GetAll_ContainsHelpCommand()
    {
        var all = _registry.GetAll();

        await Assert.That(all.Any(c => c.Command == "/help")).IsTrue();
    }

    [Test]
    public async Task GetAll_ContainsExitCommand()
    {
        var all = _registry.GetAll();

        await Assert.That(all.Any(c => c.Command == "/exit")).IsTrue();
    }

    [Test]
    public async Task Search_EmptyPrefix_ReturnsAll()
    {
        var results = _registry.Search(string.Empty);

        await Assert.That(results.Count).IsEqualTo(_registry.GetAll().Count);
    }

    [Test]
    public async Task Search_SlashPrefix_ReturnsMatches()
    {
        var results = _registry.Search("/qu");

        await Assert.That(results.Count).IsGreaterThanOrEqualTo(2);
        await Assert.That(results.Any(c => c.Command == "/queue")).IsTrue();
        await Assert.That(results.Any(c => c.Command == "/quit")).IsTrue();
    }

    [Test]
    public async Task Search_WithoutSlash_NormalizesAndReturnsMatches()
    {
        var results = _registry.Search("qu");

        await Assert.That(results.Count).IsGreaterThanOrEqualTo(2);
        await Assert.That(results.Any(c => c.Command == "/queue")).IsTrue();
    }

    [Test]
    public async Task Search_TaskPrefix_ReturnsSubcommands()
    {
        var results = _registry.Search("/task");

        await Assert.That(results.Count).IsGreaterThanOrEqualTo(3);
        await Assert.That(results.Any(c => c.Command == "/task run")).IsTrue();
        await Assert.That(results.Any(c => c.Command == "/task resume")).IsTrue();
        await Assert.That(results.Any(c => c.Command == "/task set-phase")).IsTrue();
    }

    [Test]
    public async Task Search_NoMatch_ReturnsEmpty()
    {
        var results = _registry.Search("/zzzzz");

        await Assert.That(results.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Search_IsCaseInsensitive()
    {
        var results = _registry.Search("/QUEUE");

        await Assert.That(results.Any(c => c.Command == "/queue")).IsTrue();
    }

    [Test]
    public async Task FindBestMatch_ExactMatch_ReturnsCommand()
    {
        var result = _registry.FindBestMatch("/queue");

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Command).IsEqualTo("/queue");
    }

    [Test]
    public async Task FindBestMatch_SubcommandMatch_ReturnsLongerMatch()
    {
        var result = _registry.FindBestMatch("/task run");

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Command).IsEqualTo("/task run");
    }

    [Test]
    public async Task FindBestMatch_WithoutSlash_NormalizesInput()
    {
        var result = _registry.FindBestMatch("queue");

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Command).IsEqualTo("/queue");
    }

    [Test]
    public async Task FindBestMatch_NullOrEmpty_ReturnsNull()
    {
        var result = _registry.FindBestMatch(string.Empty);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetCategories_ReturnsDistinctCategories()
    {
        var categories = _registry.GetCategories();

        await Assert.That(categories.Count).IsGreaterThanOrEqualTo(5);
        await Assert.That(categories).Contains("Interactive");
        await Assert.That(categories).Contains("Queue");
        await Assert.That(categories).Contains("Task");
        await Assert.That(categories).Contains("Diagnostics");
    }

    [Test]
    public async Task GetByCategory_Interactive_ReturnsBuiltins()
    {
        var commands = _registry.GetByCategory("Interactive");

        await Assert.That(commands.Count).IsGreaterThanOrEqualTo(3);
        await Assert.That(commands.All(c => c.IsBuiltin)).IsTrue();
    }

    [Test]
    public async Task GetByCategory_Queue_ReturnsQueueCommands()
    {
        var commands = _registry.GetByCategory("Queue");

        await Assert.That(commands.Any(c => c.Command == "/queue")).IsTrue();
        await Assert.That(commands.Any(c => c.Command == "/run")).IsTrue();
    }

    [Test]
    public async Task AllCommands_HaveDescriptions()
    {
        var all = _registry.GetAll();

        foreach (var cmd in all)
        {
            await Assert.That(cmd.Description).IsNotEmpty();
        }
    }

    [Test]
    public async Task AllCommands_HaveCategories()
    {
        var all = _registry.GetAll();

        foreach (var cmd in all)
        {
            await Assert.That(cmd.Category).IsNotEmpty();
        }
    }

    [Test]
    public async Task AllCommands_StartWithSlash()
    {
        var all = _registry.GetAll();

        foreach (var cmd in all)
        {
            await Assert.That(cmd.Command.StartsWith('/')).IsTrue();
        }
    }
}
