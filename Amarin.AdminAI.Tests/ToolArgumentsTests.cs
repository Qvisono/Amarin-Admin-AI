using System.Text.Json;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Аргументы вызова пишет языковая модель, и пишет их не всегда чисто. Здесь собраны все
/// поломки, из-за которых ход раньше срывался с «ожидался JSON».
/// </summary>
public sealed class ToolArgumentsTests
{
    [Fact]
    public void A_clean_object_is_read_as_is()
    {
        var arguments = ToolArguments.Parse("""{"prompt":"проверь диск","complexity":"lite"}""");

        Assert.Equal("проверь диск", arguments.GetProperty("prompt").GetString());
        Assert.Equal("lite", arguments.GetProperty("complexity").GetString());
    }

    [Fact]
    public void An_extra_closing_brace_is_ignored()
    {
        // Ровно то, что показывала ошибка: «'}' is invalid after a value».
        var arguments = ToolArguments.Parse("""{"complexity":"lite"}}""");

        Assert.Equal("lite", arguments.GetProperty("complexity").GetString());
    }

    [Fact]
    public void Two_objects_glued_together_keep_the_first()
    {
        var arguments = ToolArguments.Parse("""{"complexity":"lite"}{"complexity":"heavy"}""");

        Assert.Equal("lite", arguments.GetProperty("complexity").GetString());
    }

    [Fact]
    public void Text_after_the_object_is_dropped()
    {
        var arguments = ToolArguments.Parse("""{"complexity":"heavy"} — запускаю агента""");

        Assert.Equal("heavy", arguments.GetProperty("complexity").GetString());
    }

    [Fact]
    public void Text_before_the_object_is_dropped()
    {
        var arguments = ToolArguments.Parse("""Аргументы: {"complexity":"heavy"}""");

        Assert.Equal("heavy", arguments.GetProperty("complexity").GetString());
    }

    [Fact]
    public void A_markdown_fence_is_peeled_off()
    {
        var arguments = ToolArguments.Parse("```json\n{\"complexity\":\"lite\"}\n```");

        Assert.Equal("lite", arguments.GetProperty("complexity").GetString());
    }

    [Fact]
    public void Arguments_encoded_as_a_string_are_unwrapped()
    {
        var arguments = ToolArguments.Parse("\"{\\\"complexity\\\":\\\"lite\\\"}\"");

        Assert.Equal("lite", arguments.GetProperty("complexity").GetString());
    }

    [Fact]
    public void A_cut_off_object_keeps_what_it_already_said()
    {
        // Поток оборвался на середине: то, что модель успела назвать, теряться не должно.
        var arguments = ToolArguments.Parse("""{"complexity":"lite","prompt":"проверь диск""");

        Assert.Equal("lite", arguments.GetProperty("complexity").GetString());
        Assert.Equal("проверь диск", arguments.GetProperty("prompt").GetString());
    }

    [Fact]
    public void A_trailing_comma_is_forgiven()
    {
        var arguments = ToolArguments.Parse("""{"complexity":"lite",}""");

        Assert.Equal("lite", arguments.GetProperty("complexity").GetString());
    }

    [Fact]
    public void A_windows_path_with_braces_is_not_mistaken_for_markup()
    {
        var arguments = ToolArguments.Parse("""{"path":"C:\\Program Files\\{app}\\x.exe"}""");

        Assert.Equal(@"C:\Program Files\{app}\x.exe", arguments.GetProperty("path").GetString());
    }

    [Fact]
    public void Nothing_at_all_is_an_empty_object()
    {
        Assert.Equal(JsonValueKind.Object, ToolArguments.Parse("").ValueKind);
        Assert.Equal(JsonValueKind.Object, ToolArguments.Parse(null).ValueKind);
        Assert.Equal(JsonValueKind.Object, ToolArguments.Parse("   ").ValueKind);
    }

    [Fact]
    public void An_error_string_is_still_an_error()
    {
        // Не «почти JSON», а результат другого вызова — чинить тут нечего.
        Assert.Throws<JsonException>(() => ToolArguments.Parse("ERROR: сеть недоступна"));
    }
}

/// <summary>
/// Сборка вызовов инструментов из потока. Часть моделей нумерует все вызовы нулём и различает
/// их только по <c>id</c> — без этого аргументы двух вызовов склеивались в одну строку.
/// </summary>
public sealed class ChatStreamToolCallTests
{
    [Fact]
    public void Two_calls_sharing_one_index_stay_separate()
    {
        var accumulator = new ChatStreamAccumulator();

        accumulator.Apply(Chunk(0, "call_1", "init_agent", """{"complexity":"""));
        accumulator.Apply(Chunk(0, null, null, "\"lite\"}"));
        accumulator.Apply(Chunk(0, "call_2", "init_agent", """{"complexity":"heavy"}"""));

        var calls = accumulator.BuildToolCalls();

        Assert.Equal(2, calls.Count);
        Assert.Equal("""{"complexity":"lite"}""", calls[0].Function.Arguments);
        Assert.Equal("""{"complexity":"heavy"}""", calls[1].Function.Arguments);
        Assert.Equal("init_agent", calls[0].Function.Name);
        Assert.Equal("init_agent", calls[1].Function.Name);
    }

    [Fact]
    public void Calls_on_their_own_indexes_still_work()
    {
        var accumulator = new ChatStreamAccumulator();

        accumulator.Apply(Chunk(0, "a", "read_file", """{"path":"a"}"""));
        accumulator.Apply(Chunk(1, "b", "read_file", """{"path":"b"}"""));

        var calls = accumulator.BuildToolCalls();

        Assert.Equal(2, calls.Count);
        Assert.Equal("""{"path":"a"}""", calls[0].Function.Arguments);
        Assert.Equal("""{"path":"b"}""", calls[1].Function.Arguments);
    }

    [Fact]
    public void A_name_repeated_in_every_chunk_is_not_doubled()
    {
        var accumulator = new ChatStreamAccumulator();

        accumulator.Apply(Chunk(0, "a", "init_agent", """{"complexity":"""));
        accumulator.Apply(Chunk(0, "a", "init_agent", "\"lite\"}"));

        var call = Assert.Single(accumulator.BuildToolCalls());
        Assert.Equal("init_agent", call.Function.Name);
    }

    [Fact]
    public void A_name_split_across_chunks_is_joined()
    {
        var accumulator = new ChatStreamAccumulator();

        accumulator.Apply(Chunk(0, "a", "init_", null));
        accumulator.Apply(Chunk(0, null, "agent", """{"complexity":"lite"}"""));

        var call = Assert.Single(accumulator.BuildToolCalls());
        Assert.Equal("init_agent", call.Function.Name);
    }

    private static ChatCompletionChunk Chunk(int index, string? id, string? name, string? arguments) =>
        new()
        {
            Choices =
            [
                new ChatChunkChoice
                {
                    Delta = new ChatMessageDelta
                    {
                        ToolCalls =
                        [
                            new ToolCallDelta
                            {
                                Index = index,
                                Id = id,
                                Function = new FunctionCallDelta { Name = name, Arguments = arguments }
                            }
                        ]
                    }
                }
            ]
        };
}
