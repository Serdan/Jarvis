using JarvisClient;
using Moq;
using Shared;
using Shared.Messages;

namespace JarvisClientTests
{
    [TestClass]
    public class SectionReplaceTests
    {
        [TestMethod]
        public void TestSectionReplace()
        {
            var mockFileSystem = new Mock<IFileSystem>();
            mockFileSystem.Setup(fs => fs.ReadAllText(It.IsAny<string>())).Returns("file content");

            // Arrange
            var browser = new ProjectBrowser(mockFileSystem.Object, "testDirectory");
            const string filePath = "testFile.txt";
            var sectionIdentifiers = new SectionIdentifiers("<!-- START -->", "<!-- END -->");
            const string replacementContent = "New content";
            const bool backupOption = true;

            // Act
            var result = browser.ReplaceSection(filePath, sectionIdentifiers, replacementContent, backupOption);

            // Assert
            Assert.AreEqual("Section replaced successfully.", result.IfError(x => x.Message));
        }
    }
}
