using System.IO;

// 设置你要清理的根目录路径
string targetPath = @"I:\Pictures\Pure_Media";

if (Directory.Exists(targetPath))
{
    Console.WriteLine("开始清理空文件夹...");
    DeleteEmptyDirectories(targetPath);
    Console.WriteLine("清理完成。");
}
else
{
    Console.WriteLine("指定的路径不存在。");
}

void DeleteEmptyDirectories(string startLocation)
{
    // 1. 递归进入所有子目录
    foreach (var directory in Directory.GetDirectories(startLocation))
    {
        DeleteEmptyDirectories(directory);
    }

    // 2. 检查当前目录在子目录清理完后是否为空
    // 如果没有文件且没有子目录，则删除
    if (!Directory.EnumerateFileSystemEntries(startLocation).Any())
    {
        try
        {
            Directory.Delete(startLocation);
            Console.WriteLine($"已删除空文件夹: {startLocation}");
        }
        catch (UnauthorizedAccessException)
        {
            Console.WriteLine($"权限不足，无法删除: {startLocation}");
        }
        catch (IOException e)
        {
            Console.WriteLine($"删除失败 {startLocation}: {e.Message}");
        }
    }
}