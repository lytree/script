#:package Microsoft.Playwright@1.57.0
#:package HtmlAgilityPack@1.12.4




// 这等同于在命令行运行: playwright install
var exitCode = Microsoft.Playwright.Program.Main(["install"]);

if (exitCode == 0)
{
    Console.WriteLine("浏览器安装成功！");
}