using JarvisClient;
using JarvisClientTests.Impl;

namespace JarvisClientTests;

[TestClass]
public class InsertAfterTests
{
    [TestMethod]
    public void Test()
    {
        const string root = @"C:\repos";
        const string projectName = "Test Project";
        const string fileName = "testFile.txt";
        const string filePath = $@"{root}\{projectName}\{fileName}";
        const string replacementContent = " NEW CONTENT ";
        const string originalContent = """
            THIS IS A TEST
                INSERT AFTERINSERT BEFORE
            END TEST CONTENT
            """;

        const string replacedContent = $"""
            THIS IS A TEST
                INSERT AFTER{replacementContent}INSERT BEFORE
            END TEST CONTENT
            """;

        var fileSystem = new FakeFileSystem();
        fileSystem.AddFolder(root, projectName);
        fileSystem.AddFile(filePath, originalContent);

        // Arrange
        var browser = new ProjectBrowser(fileSystem, root);

        // Act
        var result = browser.InsertAfter(projectName, fileName, "INSERT AFTER", replacementContent);

        // Assert
        Assert.AreEqual(replacedContent, result.IfError(x => x.Message));
    }
}
