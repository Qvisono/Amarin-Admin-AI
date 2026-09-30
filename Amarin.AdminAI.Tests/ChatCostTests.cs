using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>Цена чата целиком (E3): все варианты, агенты, служебная работа и старые записи.</summary>
public sealed class ChatCostTests
{
    private static VeniceCost Usd(decimal value) => new() { Usd = value, HasData = true };

    private static ChatSession Chat()
    {
        var hidden = new ChatDisplayMessage
        {
            Id = "a1-old",
            Role = "assistant",
            RequestedModelId = "grok-4-6",
            Cost = Usd(0.10m),
            ModelCost = Usd(0.10m)
        };

        var answer = new ChatDisplayMessage
        {
            Id = "a1",
            Role = "assistant",
            RequestedModelId = "grok-4-6",
            Cost = Usd(0.61m),
            ModelCost = Usd(0.05m),
            TitleCost = Usd(0.01m),
            Variants = [new ChatBranch { Messages = [hidden] }],
            ToolRounds =
            [
                new ToolRound
                {
                    Calls =
                    [
                        new ToolCallRecord { Id = "c1", Name = "init_agent", NestedAgent = new AgentRunRecord { Cost = Usd(0.50m) } },
                        new ToolCallRecord { Id = "c2", Name = "web_search", Cost = Usd(0.05m) }
                    ]
                }
            ]
        };

        // Ответ из переписки прежних версий: цена есть, разбивки нет.
        var legacy = new ChatDisplayMessage { Id = "a2", Role = "assistant", Cost = Usd(0.20m) };

        var session = new ChatSession();
        session.Messages.Add(new ChatDisplayMessage { Id = "u1", Role = "user", Text = "q" });
        session.Messages.Add(answer);
        session.Messages.Add(legacy);
        return session;
    }

    [Fact]
    public void The_total_counts_hidden_variants_too()
    {
        Assert.Equal(0.91m, ChatCost.Total(Chat()));
    }

    [Fact]
    public void The_breakdown_adds_up_to_the_total_and_names_what_it_cannot_split()
    {
        var lines = ChatCost.Breakdown(Chat());

        Assert.Equal(Loc.Get("S.Cost.Total"), lines[^1].Label);
        Assert.Equal(0.91m, lines[^1].Cost.Usd);
        Assert.Equal(0.91m, lines.Take(lines.Count - 1).Sum(line => line.Cost.Usd));
        Assert.Contains(lines, line => line.Label == Loc.Get("S.Cost.Agents") && line.Cost.Usd == 0.50m);
        Assert.Contains(lines, line => line.Label == Loc.Get("S.Cost.Tools") && line.Cost.Usd == 0.05m);
        Assert.Contains(lines, line => line.Label == Loc.Get("S.Cost.ChatTitle") && line.Cost.Usd == 0.01m);
        Assert.Contains(lines, line => line.Label == Loc.Get("S.Cost.Other") && line.Cost.Usd == 0.20m);

        // Одна модель в двух вариантах — одна строка.
        Assert.Single(lines, line => line.Cost.Usd == 0.15m);
    }

    [Fact]
    public void A_chat_that_cost_nothing_has_only_a_zero_total()
    {
        var lines = ChatCost.Breakdown(new ChatSession());

        Assert.Equal(0m, Assert.Single(lines).Cost.Usd);
    }
}
