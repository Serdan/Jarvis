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
        const string originalContent = """
            THIS IS A TEST
                <!-- START -->THIS SHOULD BE REPLACED<!-- END -->
            END TEST CONTENT
            """;

        const string replacedContent = """
            THIS IS A TEST
                NEW CONTENT<!-- END -->
            END TEST CONTENT
            """;

        var fileSystem = new FakeFileSystem();
        fileSystem.AddFolder(@"C:\repos", projectName);
        fileSystem.AddFile(filePath, originalContent);

        // Arrange
        var browser = new ProjectBrowser(fileSystem, @"C:\repos");
        var sectionIdentifiers = new SectionIdentifiers("<!-- START -->", "<!-- END -->");
        const string replacementContent = "NEW CONTENT";

        // Act
        var result = browser.ReplaceSection(projectName, fileName, sectionIdentifiers, replacementContent);

        // Assert
        Assert.AreEqual("Section replaced successfully.", result.IfError(x => x.Message));
        Assert.AreEqual(replacedContent, fileSystem.ReadAllText(filePath));
    }
}
