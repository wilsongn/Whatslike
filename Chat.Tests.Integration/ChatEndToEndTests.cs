using System.Text;
using FluentAssertions;

namespace Chat.Tests.Integration;

[Collection("server")]
public sealed class ChatEndToEndTests
{
    private readonly TestServerFixture _fx;
    public ChatEndToEndTests(TestServerFixture fx) => _fx = fx;

    [Fact(Timeout = 20000)]
    public async Task Private_message_flows_between_two_clients()
    {
        await using var alice = new TestChatClient("alice");
        await using var bob = new TestChatClient("bob");

        await alice.ConnectAsync("127.0.0.1", _fx.Port);
        await bob.ConnectAsync("127.0.0.1", _fx.Port);

        await alice.SendPrivateAsync("bob", "olá, bob!");

        // aguarda até chegar
        await Eventually(() => bob.Inbox.Any(x => x.EndsWith(":olá, bob!")));

        bob.Inbox.Should().Contain(i => i.StartsWith("alice:") && i.EndsWith("olá, bob!"));
    }

    [Fact(Timeout = 20000)]
    public async Task Group_message_reaches_member()
    {
        await using var alice = new TestChatClient("alice");
        await using var bob = new TestChatClient("bob");

        await alice.ConnectAsync("127.0.0.1", _fx.Port);
        await bob.ConnectAsync("127.0.0.1", _fx.Port);

        await alice.CreateGroupAsync("sala");
        await alice.AddToGroupAsync("sala", "bob");

        await alice.SendGroupAsync("sala", "bem-vindos");

        await Eventually(() => bob.GroupsInbox.Any(x => x.StartsWith("sala:alice:")));

        bob.GroupsInbox.Should().Contain(i => i == "sala:alice:bem-vindos");
    }

    [Fact(Timeout = 30000)]
    public async Task File_header_is_received_by_target()
    {
        await using var alice = new TestChatClient("alice");
        await using var bob = new TestChatClient("bob");

        await alice.ConnectAsync("127.0.0.1", _fx.Port);
        await bob.ConnectAsync("127.0.0.1", _fx.Port);

        var data = Encoding.UTF8.GetBytes("conteudo de teste");
        await alice.SendFileAsync("bob", data, "teste.txt");

        await Eventually(() => bob.Files.Any(f => f.Contains("header:teste.txt")));

        bob.Files.Should().Contain(f => f.StartsWith("header:teste.txt"));
    }

    // helper: espera condição ficar true (polling simples)
    private static async Task Eventually(Func<bool> predicate, int millis = 2000, int step = 50)
    {
        var waited = 0;
        while (waited < millis)
        {
            if (predicate()) return;
            await Task.Delay(step);
            waited += step;
        }
        throw new TimeoutException("Condição não satisfeita a tempo.");
    }
}
