using JsonToolbox.App.ViewModels;

namespace JsonToolbox.App.Tests;

public sealed class MainWindowViewModelTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("jsontoolbox-tabs-").FullName;

    private string File(string name)
    {
        string path = Path.Combine(_folder, name);
        System.IO.File.WriteAllText(path, """{"file":"%NAME%"}""".Replace("%NAME%", name));
        return path;
    }

    private static IEnumerable<string> Tabs(MainWindowViewModel window) => window.Documents.Select(d => d.Name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A session still holding a file open is a test's own failure to close it.
        }
    }

    [Fact]
    public void A_closed_document_comes_back_where_it_was()
    {
        var window = new MainWindowViewModel();
        window.OpenPath(File("a.json"));
        window.OpenPath(File("b.json"));
        window.OpenPath(File("c.json"));

        DocumentSession middle = window.Documents[1];
        window.CloseDocumentCommand.Execute(middle);
        Assert.Equal(["a.json", "c.json"], Tabs(window));

        window.ReopenClosedCommand.Execute(null);

        Assert.Equal(["a.json", "b.json", "c.json"], Tabs(window));
        Assert.Equal("b.json", window.ActiveDocument?.Name);
        Assert.NotSame(middle, window.ActiveDocument);

        CloseAll(window);
    }

    [Fact]
    public void Closed_documents_come_back_most_recent_first()
    {
        var window = new MainWindowViewModel();
        window.OpenPath(File("a.json"));
        window.OpenPath(File("b.json"));

        window.CloseDocumentCommand.Execute(window.Documents[0]);
        window.CloseDocumentCommand.Execute(window.Documents[0]);
        Assert.Empty(window.Documents);

        window.ReopenClosedCommand.Execute(null);
        Assert.Equal(["b.json"], Tabs(window));

        window.ReopenClosedCommand.Execute(null);
        Assert.Equal(["a.json", "b.json"], Tabs(window));

        window.ReopenClosedCommand.Execute(null);
        Assert.Equal(["a.json", "b.json"], Tabs(window));
        Assert.Equal("Nothing has been closed.", window.StatusText);

        CloseAll(window);
    }

    [Fact]
    public void Reopening_a_document_that_is_open_again_just_shows_it()
    {
        var window = new MainWindowViewModel();
        string a = File("a.json");
        window.OpenPath(a);
        window.OpenPath(File("b.json"));

        window.CloseDocumentCommand.Execute(window.Documents[0]);
        window.OpenPath(a);
        window.ReopenClosedCommand.Execute(null);

        Assert.Equal(["b.json", "a.json"], Tabs(window));

        CloseAll(window);
    }

    [Fact]
    public void Moving_a_tab_reorders_the_documents_and_the_comparison_choices()
    {
        var window = new MainWindowViewModel();
        window.OpenPath(File("a.json"));
        window.OpenPath(File("b.json"));
        window.OpenPath(File("c.json"));
        window.CompareCommand.Execute(null);

        window.MoveDocument(window.Documents[0], 2);

        Assert.Equal(["b.json", "c.json", "a.json"], Tabs(window));
        Assert.Equal(["b.json", "c.json", "a.json"], window.Comparison.Choices.Select(c => c.Session?.Name));

        window.MoveDocument(window.Documents[2], 0);
        Assert.Equal(["a.json", "b.json", "c.json"], Tabs(window));

        // A position past either end means the end.
        window.MoveDocument(window.Documents[0], 99);
        Assert.Equal(["b.json", "c.json", "a.json"], Tabs(window));

        CloseAll(window);
    }

    [Fact]
    public void Closing_the_view_closes_the_comparison_when_that_is_what_is_shown()
    {
        var window = new MainWindowViewModel();
        window.OpenPath(File("a.json"));
        window.CompareCommand.Execute(null);
        Assert.True(window.IsDiffOpen);

        window.CloseViewCommand.Execute(null);

        Assert.False(window.HasComparison);
        Assert.Equal(["a.json"], Tabs(window));

        window.CloseViewCommand.Execute(null);
        Assert.Empty(window.Documents);
    }

    private static void CloseAll(MainWindowViewModel window)
    {
        while (window.Documents.Count > 0)
        {
            window.CloseDocumentCommand.Execute(window.Documents[0]);
        }
    }
}
