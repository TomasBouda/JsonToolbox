using JsonToolbox.App;

namespace JsonToolbox.App.Tests;

public class StartupOptionsTests
{
    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("-?")]
    [InlineData("/?")]
    public void Recognises_the_ways_of_asking_for_help(string flag)
    {
        StartupOptions options = StartupOptions.Parse([flag]);

        Assert.True(options.Help);
        Assert.Null(options.Problem);
    }

    [Fact]
    public void Asking_for_help_is_answered_with_help_whatever_else_is_wrong()
    {
        // Somebody who types --help has already said they do not know what to type; complaining
        // about the rest of the line first would be answering a question they did not ask.
        StartupOptions options = StartupOptions.Parse(["--compare", "only-one.json", "--help"]);

        Assert.True(options.Help);
        Assert.Null(options.Problem);
    }

    [Fact]
    public void The_help_text_says_how_to_compare()
    {
        Assert.Contains("--compare", StartupOptions.HelpText);
        Assert.Contains("Usage:", StartupOptions.HelpText);
    }

    [Fact]
    public void No_arguments_is_not_a_problem()
    {
        StartupOptions options = StartupOptions.Parse([]);

        Assert.Empty(options.Paths);
        Assert.False(options.Compare);
        Assert.Null(options.Problem);
    }

    [Fact]
    public void Keeps_the_paths_in_the_order_they_were_given()
    {
        StartupOptions options = StartupOptions.Parse(["b.json", "a.json"]);

        Assert.Equal(["b.json", "a.json"], options.Paths);
        Assert.Null(options.Problem);
    }

    [Theory]
    [InlineData("--compare")]
    [InlineData("-c")]
    [InlineData("--diff")]
    [InlineData("--COMPARE")]
    public void Recognises_the_ways_of_asking_to_compare(string flag)
    {
        StartupOptions options = StartupOptions.Parse([flag, "a.json", "b.json"]);

        Assert.True(options.Compare);
        Assert.Equal(["a.json", "b.json"], options.Paths);
        Assert.Null(options.Problem);
    }

    [Fact]
    public void The_flag_may_come_after_the_files()
    {
        StartupOptions options = StartupOptions.Parse(["a.json", "b.json", "--compare"]);

        Assert.True(options.Compare);
        Assert.Equal(["a.json", "b.json"], options.Paths);
    }

    [Fact]
    public void Comparing_one_file_says_what_is_missing()
    {
        StartupOptions options = StartupOptions.Parse(["--compare", "a.json"]);

        Assert.True(options.Compare);
        Assert.Contains("two files", options.Problem);
    }

    [Fact]
    public void Comparing_nothing_says_so_too()
    {
        StartupOptions options = StartupOptions.Parse(["--compare"]);

        Assert.Contains("two files", options.Problem);
    }

    [Fact]
    public void Comparing_more_than_two_files_opens_them_all_and_says_which_pair_it_took()
    {
        StartupOptions options = StartupOptions.Parse(["--compare", "a.json", "b.json", "c.json"]);

        Assert.Equal(3, options.Paths.Count);
        Assert.Contains("first two", options.Problem);
    }

    [Fact]
    public void An_unknown_switch_is_reported_rather_than_taken_for_a_file()
    {
        StartupOptions options = StartupOptions.Parse(["--dif", "a.json"]);

        Assert.Equal(["a.json"], options.Paths);
        Assert.Contains("--dif", options.Problem);
    }

    [Fact]
    public void Empty_arguments_are_ignored()
    {
        // A shell that expands an unset variable hands over an empty string; opening a file
        // called nothing is not what was meant.
        StartupOptions options = StartupOptions.Parse(["", "  ", "a.json"]);

        Assert.Equal(["a.json"], options.Paths);
        Assert.Null(options.Problem);
    }

    [Fact]
    public void Survives_a_null_argument_list()
    {
        StartupOptions options = StartupOptions.Parse(null);

        Assert.Empty(options.Paths);
        Assert.Null(options.Problem);
    }
}
