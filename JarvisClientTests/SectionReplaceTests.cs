using JarvisClient;
using JarvisClientTests.Impl;
using Shared.Messages;

namespace JarvisClientTests;

[TestClass]
public class SectionReplaceTests
{
    [TestMethod]
    public void TestSectionReplace()
    {
        const string projectName = "Test Project";
        const string fileName = "testFile.txt";
        const string filePath = $@"C:\repos\{projectName}\{fileName}";

        const string start = "<!-- START -->";
        const string end = "<!-- END -->";
        const string replacementContent = "NEW CONTENT";
        const string originalContent = $"""
            THIS IS A TEST
                {start}THIS SHOULD BE REPLACED{end}
            END TEST CONTENT
            """;

        const string replacedContent = $"""
            THIS IS A TEST
                {replacementContent}
            END TEST CONTENT
            """;

        var fileSystem = new FakeFileSystem();
        fileSystem.AddFolder(@"C:\repos", projectName);
        fileSystem.AddFile(filePath, originalContent);

        // Arrange
        var browser = new ProjectBrowser(fileSystem, @"C:\repos");
        var sectionIdentifiers = new SectionIdentifiers(start, end);

        // Act
        var result = browser.ReplaceSection(projectName, fileName, sectionIdentifiers, replacementContent);

        // Assert
        Assert.AreEqual(replacedContent, result.IfError(x => x.Message));
    }

    [TestMethod]
    public void TestSectionReplaceEqualIdentifiers()
    {
        const string projectName = "Test Project";
        const string fileName = "testFile.txt";
        const string filePath = $@"C:\repos\{projectName}\{fileName}";

        const string identifier = "<!-- START -->THIS SHOULD BE REPLACED<!-- END -->";
        const string replacementContent = "NEW CONTENT";
        const string originalContent = $"""
            THIS IS A TEST
                {identifier}
            END TEST CONTENT
            """;

        const string replacedContent = $"""
            THIS IS A TEST
                {replacementContent}
            END TEST CONTENT
            """;

        var fileSystem = new FakeFileSystem();
        fileSystem.AddFolder(@"C:\repos", projectName);
        fileSystem.AddFile(filePath, originalContent);

        // Arrange
        var browser = new ProjectBrowser(fileSystem, @"C:\repos");
        var sectionIdentifiers = new SectionIdentifiers(identifier, identifier);
        

        // Act
        var result = browser.ReplaceSection(projectName, fileName, sectionIdentifiers, replacementContent);

        // Assert
        Assert.AreEqual(replacedContent, result.IfError(x => x.Message));
    }
}
