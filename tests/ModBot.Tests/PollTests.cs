using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModBot.Configuration;
using ModBot.Services.Chat;
using ModBot.Services.Polls;
using ModBot.Services.Reminders;

namespace ModBot.Tests;

public class PollRulesTests
{
    [Fact]
    public void Tidies_answers()
    {
        var check = PollRules.Check("  What map?  ", [" Rust ", "", "Shipment", "rust"], 24, false);

        Assert.NotNull(check.Draft);
        Assert.Equal("What map?", check.Draft.Question);
        Assert.Equal(["Rust", "Shipment"], check.Draft.Answers);
    }

    [Theory]
    [InlineData("", 24, "What should the poll ask?")]
    [InlineData("Q?", 0, "Polls can run from 1 hour to 32 days.")]
    [InlineData("Q?", 769, "Polls can run from 1 hour to 32 days.")]
    public void Rejects_bad_polls(string question, int hours, string problem)
        => Assert.Equal(problem, PollRules.Check(question, ["a", "b"], hours, false).Problem);

    [Fact]
    public void Needs_two_different_answers()
        => Assert.Null(PollRules.Check("Q?", ["same", "Same"], 24, false).Draft);

    [Fact]
    public void Rejects_more_than_ten_answers()
        => Assert.Null(PollRules.Check("Q?", Enumerable.Range(1, 11).Select(i => $"a{i}"), 24, false).Draft);

    [Fact]
    public void Rejects_long_answers_instead_of_cutting_them()
        => Assert.Null(PollRules.Check("Q?", ["a", new string('x', PollRules.MaxAnswerLength + 1)], 24, false).Draft);

    [Fact]
    public void The_long_answer_message_never_splits_an_emoji()
    {
        // The emoji's two halves are characters 20 and 21, right where the answer is cut.
        var problem = PollRules.Check("Q?", ["a", new string('x', 19) + "🍕" + new string('x', 40)], 24, false).Problem;

        Assert.NotNull(problem);
        // A half emoji doesn't survive a round trip through UTF-8.
        Assert.Equal(problem, System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(problem)));
    }

    [Theory]
    [InlineData("a, b, c", new[] { "a", "b", "c" })]
    [InlineData("Yes, tonight | No", new[] { "Yes, tonight", "No" })]
    public void Splits_answers(string text, string[] expected) => Assert.Equal(expected, PollRules.SplitAnswers(text));
}

public class PollRequestReaderTests
{
    [Theory]
    [InlineData("make a poll: what should we play tonight? ranked, casual or zombies", "what should we play tonight?",
        new[] { "ranked", "casual", "zombies" })]
    [InlineData("can you start a poll asking which map? rust | shipment | nuketown", "which map?",
        new[] { "rust", "shipment", "nuketown" })]
    [InlineData("poll: best gun? AK, M4, or MP5", "best gun?", new[] { "AK", "M4", "MP5" })]
    [InlineData("poll everyone - game night friday? yes, no, maybe", "game night friday?", new[] { "yes", "no", "maybe" })]
    [InlineData("create a poll for 2 days about pizza toppings options: pepperoni, pineapple", "pizza toppings",
        new[] { "pepperoni", "pineapple" })]
    [InlineData("make a poll\nwhat game?\nwarzone\nmultiplayer", "what game?", new[] { "warzone", "multiplayer" })]
    public void Reads_laid_out_polls(string message, string question, string[] answers)
    {
        var request = PollRequestReader.Read(message);

        Assert.NotNull(request);
        Assert.Equal(question, request.Question);
        Assert.Equal(answers, request.Answers);
    }

    [Theory]
    [InlineData("make a poll for 2 days: ranked or casual?", 48)]
    [InlineData("start a poll for an hour about dinner", 1)]
    [InlineData("make a poll for a week - should we do a tournament?", 168)]
    [InlineData("make a poll for 6h about the map", 6)]
    public void Reads_lengths(string message, int hours) => Assert.Equal(hours, PollRequestReader.Read(message)?.Hours);

    [Fact]
    public void Leaves_the_rest_for_the_model_when_not_laid_out()
    {
        var request = PollRequestReader.Read("hey can you make a quick poll multiple choice for 3 days on whether we should play ranked or casual tonight");

        Assert.NotNull(request);
        Assert.Null(request.Question);
        Assert.True(request.AllowMultiple);
        Assert.Equal(72, request.Hours);
        Assert.Equal("whether we should play ranked or casual tonight", request.Text);
    }

    [Fact]
    public void Uses_what_came_before_the_phrase_when_nothing_follows()
        => Assert.Equal("is game night friday?", PollRequestReader.Read("is game night friday? make a poll")?.Text);

    [Fact]
    public void Settings_after_the_phrase_dont_stop_it_using_what_came_before()
    {
        var request = PollRequestReader.Read("ranked or casual? make a poll for 2 days with multiple answers");

        Assert.NotNull(request);
        Assert.Equal("ranked or casual?", request.Text);
        Assert.Equal(48, request.Hours);
        Assert.True(request.AllowMultiple);
    }

    [Theory]
    [InlineData("is game night friday? can you make a poll?", "is game night friday?")]
    [InlineData("pizza or tacos? make a poll pls", "pizza or tacos?")]
    [InlineData("pizza or tacos? make a poll now please!", "pizza or tacos?")]
    public void Punctuation_or_please_after_the_phrase_isnt_the_poll(string message, string text)
        => Assert.Equal(text, PollRequestReader.Read(message)?.Text);

    [Fact]
    public void A_length_shaped_last_answer_stays_an_answer()
    {
        var request = PollRequestReader.Read("make a poll: how long should the event run? for 2 days or for a week");

        Assert.NotNull(request);
        Assert.Null(request.Hours);
        Assert.Equal(["for 2 days", "for a week"], request.Answers);
    }

    [Theory]
    [InlineData("what should we play tonight? make a poll", "what should we play tonight?")]
    [InlineData("where should we eat? start a poll", "where should we eat?")]
    public void A_question_written_first_is_still_a_request(string message, string text)
        => Assert.Equal(text, PollRequestReader.Read(message)?.Text);

    [Fact]
    public void Time_spans_inside_the_question_stay_in_it()
    {
        var request = PollRequestReader.Read("make a poll: who's up for 2 hours of raid tonight? yes, no");

        Assert.NotNull(request);
        Assert.Null(request.Hours);
        Assert.Equal("who's up for 2 hours of raid tonight?", request.Question);
    }

    [Fact]
    public void A_question_ending_in_a_time_span_keeps_it()
        => Assert.Null(PollRequestReader.Read("make a poll: anyone free for 2 hours?")?.Hours);

    [Theory]
    [InlineData("make a multiple choice poll: which maps? rust, shipment or nuketown")]
    [InlineData("create a multi-select poll: which maps? rust, shipment or nuketown")]
    [InlineData("make a poll: which maps? rust, shipment or nuketown, multiple choice")]
    public void Reads_multiple_choice(string message)
    {
        var request = PollRequestReader.Read(message);

        Assert.NotNull(request);
        Assert.True(request.AllowMultiple);
        Assert.Equal(["rust", "shipment", "nuketown"], request.Answers);
    }

    [Theory]
    [InlineData("how do i make a poll?")]
    [InlineData("can i make a poll in here")]
    [InlineData("is there a way to start a poll")]
    [InlineData("the poll was rigged lol")]
    [InlineData("what did the poll say")]
    [InlineData("i voted in the poll")]
    [InlineData("start a vote to skip this song")] // the music's vote skip
    [InlineData("make a vote skip happen")]
    public void Ignores_non_requests(string message) => Assert.Null(PollRequestReader.Read(message));

    [Theory]
    [InlineData("end my poll", true)]
    [InlineData("close the poll please", true)]
    [InlineData("can you stop this vote", true)]
    [InlineData("hey, can you end the poll now please", true)]
    [InlineData("how do i end my poll?", false)]
    [InlineData("the poll ends tomorrow", false)]
    [InlineData("don't end my poll yet", false)]
    [InlineData("who's going to close the poll?", false)]
    [InlineData("i'll end the poll later", false)]
    public void Spots_end_requests(string message, bool expected)
        => Assert.Equal(expected, PollRequestReader.IsEndRequest(message));

    [Fact]
    public void Keeps_only_answers_the_person_wrote()
    {
        var kept = PollRequestReader.KeepAnswersFrom("should we play Ranked or casual tonight?",
            ["Ranked!", "Casual", "Zombies", "Rank", ""]);

        Assert.Equal(["Ranked!", "Casual"], kept);
    }

    [Fact]
    public void Drops_answers_that_are_only_words_from_the_question()
        => Assert.Empty(PollRequestReader.KeepAnswersFrom("should we play ranked tonight", ["ranked", "tonight", "play ranked"]));

    [Fact]
    public void Drops_repeated_answers()
        => Assert.Equal(["pizza"], PollRequestReader.KeepAnswersFrom("pizza or pizza", ["pizza", "Pizza"]));

    [Fact]
    public void Keeps_emoji_answers_the_person_wrote()
        => Assert.Equal(["🍕", "🌮"], PollRequestReader.KeepAnswersFrom("🍕 or 🌮", ["🍕", "🌮", "🍔"]));
}

public sealed class ChatPollParsingTests : IDisposable
{
    private readonly TempReminderStore _temp = new();

    public void Dispose() => _temp.Dispose();

    private ChatActionParser Parser(FakeChat chat)
        => new(chat, new ReminderService(_temp.Store, new FakeGuildSettings(), Options.Create(new ReminderOptions()),
            NullLogger<ReminderService>.Instance), NullLogger<ChatActionParser>.Instance);

    [Fact]
    public async Task Laid_out_polls_are_read_without_the_model()
    {
        // The fake would turn every message into an LFG if the model were asked.
        var action = await Parser(new FakeChat("""{"action":"create_lfg"}"""))
            .ParseAsync("make a poll for 2 days: what should we play? ranked, casual or zombies");

        var poll = Assert.IsType<CreatePollAction>(action);
        Assert.Equal("what should we play?", poll.Question);
        Assert.Equal(["ranked", "casual", "zombies"], poll.Answers);
        Assert.Equal(48, poll.Hours);
    }

    [Fact]
    public async Task Answers_the_model_made_up_are_dropped()
    {
        var chat = new FakeChat("""{"question":"Ranked or casual tonight?","answers":["Ranked","Casual","Zombies"]}""");

        var action = await Parser(chat).ParseAsync("make a poll on whether we play ranked or casual tonight");

        var poll = Assert.IsType<CreatePollAction>(action);
        Assert.Equal("Ranked or casual tonight?", poll.Question);
        Assert.Equal(["Ranked", "Casual"], poll.Answers);
        Assert.True(poll.FromModel); // so the word filter checks it even for moderators
    }

    [Fact]
    public async Task Repeated_model_answers_fall_back_to_yes_or_no()
    {
        var chat = new FakeChat("""{"question":"Pizza tonight?","answers":["pizza","Pizza"]}""");

        var action = await Parser(chat).ParseAsync("make a poll asking if we should get pizza");

        Assert.Equal(PollRules.YesNo, Assert.IsType<CreatePollAction>(action).Answers);
    }

    [Fact]
    public async Task No_answers_means_yes_or_no()
    {
        var chat = new FakeChat("""{"question":"Game night on Friday?","answers":["Sure","Nah"]}""");

        var action = await Parser(chat).ParseAsync("start a poll about game night on friday");

        Assert.Equal(PollRules.YesNo, Assert.IsType<CreatePollAction>(action).Answers);
    }

    [Fact]
    public async Task A_model_failure_asks_again_instead_of_guessing()
    {
        var action = await Parser(new FakeChat("not json")).ParseAsync("make a poll about the raid");

        Assert.IsType<UnclearPollAction>(action);
    }

    [Fact]
    public async Task End_requests_are_read_without_the_model()
        => Assert.IsType<EndPollAction>(await Parser(new FakeChat("""{"action":"create_lfg"}""")).ParseAsync("end my poll"));
}
