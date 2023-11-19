using JarvisClient;
using JarvisClientTests.Impl;
using Shared.Messages;

namespace JarvisClientTests;

[TestClass]
public class ReplaceTests
{
    [TestMethod]
    public void TestReplace()
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
                <!-- START -->NEW CONTENT<!-- END -->
            END TEST CONTENT
            """;

        var fileSystem = new FakeFileSystem();
        fileSystem.AddFolder(@"C:\repos", projectName);
        fileSystem.AddFile(filePath, originalContent);

        // Arrange
        var browser = new ProjectBrowser(fileSystem, @"C:\repos");
        const string replacementContent = "NEW CONTENT";

        // Act
        var result = browser.Replace(projectName, fileName, "THIS SHOULD BE REPLACED", replacementContent);
        var newContent = fileSystem.ReadAllText(filePath);

        // Assert
        Assert.AreEqual("Search string replaced successfully.", result.IfError(x => x.Message));
        Assert.AreEqual(replacedContent, newContent);
    }
}
