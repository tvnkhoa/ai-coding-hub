using AiCodingHub.Infrastructure.System;

namespace AiCodingHub.Tests;

/// <summary>Picking the right VS Code window (all owned by one Code.exe) from its title.</summary>
public class WindowTitleMatcherTests
{
    private static readonly string[] VsCodeTitles =
    [
        "Program.cs — wec.be — Visual Studio Code",
        "● SessionKiller.cs — ai-coding-hub-wt-x — Visual Studio Code",
        "README.md - ai-coding-hub - Visual Studio Code",
        "ai-coding-hub (Workspace) — Visual Studio Code",
    ];

    [Fact]
    public void Picks_Window_Naming_The_Session_Folder_As_A_Whole_Segment()
    {
        var hints = WindowTitleMatcher.FolderHints(@"D:\1.SourceCode\ai-coding-hub", "ai-coding-hub");
        Assert.Equal(2, WindowTitleMatcher.Pick(VsCodeTitles, hints)); // not the "-wt-x" window above it
    }

    [Fact]
    public void Worktree_Cwd_Leaf_Wins_Over_The_Repo_Name()
    {
        var hints = WindowTitleMatcher.FolderHints(@"D:\1.SourceCode\ai-coding-hub-wt-x\", "ai-coding-hub");
        Assert.Equal(["ai-coding-hub-wt-x", "ai-coding-hub"], hints);
        Assert.Equal(1, WindowTitleMatcher.Pick(VsCodeTitles, hints));
    }

    [Fact]
    public void Falls_Back_To_The_Repo_Name_When_The_Leaf_Has_No_Window()
    {
        var hints = WindowTitleMatcher.FolderHints(@"D:\work\other-wt-y", "wec.be");
        Assert.Equal(0, WindowTitleMatcher.Pick(VsCodeTitles, hints));
    }

    [Fact]
    public void Matches_A_Workspace_Title()
    {
        string[] titles = ["Program.cs — wec.be — Visual Studio Code", "ai-coding-hub (Workspace) — Visual Studio Code"];
        Assert.Equal(1, WindowTitleMatcher.Pick(titles, ["ai-coding-hub"]));
    }

    [Fact]
    public void Matches_An_Administrator_Title_Segment()
    {
        string[] titles = ["a — x — Visual Studio Code", "[Administrator] b.cs - ai-coding-hub - Visual Studio Code"];
        Assert.Equal(1, WindowTitleMatcher.Pick(titles, ["ai-coding-hub"]));
    }

    [Fact]
    public void No_Match_Keeps_The_Topmost_Window_And_No_Titles_Gives_Minus_One()
    {
        Assert.Equal(0, WindowTitleMatcher.Pick(VsCodeTitles, ["nothing-here"]));
        Assert.Equal(0, WindowTitleMatcher.Pick(["Windows PowerShell"], []));
        Assert.Equal(-1, WindowTitleMatcher.Pick([], ["ai-coding-hub"]));
    }

    [Fact]
    public void Substring_Is_The_Second_Tier()
    {
        string[] titles = ["Terminal", "claude in ai-coding-hub"];
        Assert.Equal(1, WindowTitleMatcher.Pick(titles, ["ai-coding-hub"]));
    }

    [Fact]
    public void Hints_Skip_Empty_And_Duplicate_Names()
    {
        Assert.Empty(WindowTitleMatcher.FolderHints(null, " "));
        Assert.Equal(["AI-Coding-Hub"], WindowTitleMatcher.FolderHints(@"D:\x\AI-Coding-Hub", "ai-coding-hub"));
    }
}
