using System.IO;
using System.Text.RegularExpressions;

// --- 配置区域 ---
string parentPath = @"H:\Pictures\"; // 替换为你的目标路径
string textToRemove = " ";       // 替换为你想删除的文字
// ----------------

if (!Directory.Exists(parentPath))
{
    Console.WriteLine("错误：目标路径不存在。");
    return;
}

// 获取子文件夹（非递归）
var directories = Directory.GetDirectories(parentPath);
int successCount = 0;

foreach (string dirPath in directories)
{
    string originalName = Path.GetFileName(dirPath);

    // 使用正则表达式执行忽略大小写的替换
    // RegexOptions.IgnoreCase 是核心
    string newName = Regex.Replace(originalName, textToRemove, "", RegexOptions.IgnoreCase).Trim();

    // 如果名称变动且不为空，则尝试重命名
    if (originalName != newName && !string.IsNullOrEmpty(newName))
    {
        string newPath = Path.Combine(parentPath, newName);

        try
        {
            if (!Directory.Exists(newPath))
            {
                Directory.Move(dirPath, newPath);
                Console.WriteLine($"[成功] {originalName} -> {newName}");
                successCount++;
            }
            else
            {
                Console.WriteLine($"[跳过] 目标已存在: {newName}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[失败] 重命名 {originalName} 时出错: {ex.Message}");
        }
    }
}

Console.WriteLine($"\n任务结束！成功重命名了 {successCount} 个文件夹。");