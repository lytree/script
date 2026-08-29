using System.Text.RegularExpressions;

// 1. 设置目标路径：优先从参数读取，否则使用当前目录
string targetPath = @"H:\Pictures";

if (!Directory.Exists(targetPath))
{
    Console.WriteLine($"[错误] 路径不存在: {targetPath}");
    return;
}

Console.WriteLine($"正在清理非法字符和 Null 字节: {targetPath}");
Console.WriteLine("--------------------------------------------");

int fixedFiles = 0;
int fixedNfos = 0;
int count = 0;
// 2. 获取所有文件（递归）
var files = Directory.EnumerateFiles(targetPath, "*.*", SearchOption.AllDirectories);

foreach (string filePath in files)
{count++;
    try
    {
        var fileInfo = new FileInfo(filePath);
        
        // --- 逻辑 A：清理文件名中的非法控制字符 ---
        // 匹配包括 Null (0x00) 在内的低位不可见字符
        string fileName = fileInfo.Name;
        if (Regex.IsMatch(fileName, @"[\x00-\x1F]"))
        {
            string cleanName = Regex.Replace(fileName, @"[\x00-\x1F]", "");
            string newPath = Path.Combine(fileInfo.DirectoryName!, cleanName);
            
            Console.WriteLine($"[文件名修复] {fileName} -> {cleanName}");
            File.Move(filePath, newPath);
            fixedFiles++;
            // 更新当前处理的文件路径，以便后续处理内容
            // filePath = newPath; 
        }

        // --- 逻辑 B：清理 NFO 文件内容中的 Null 字节 ---
        if (fileInfo.Extension.Equals(".nfo", StringComparison.OrdinalIgnoreCase))
        {
            byte[] bytes = File.ReadAllBytes(filePath);
            if (bytes.Contains((byte)0))
            {
                Console.WriteLine($"[内容清理] 修复损坏的 NFO: {fileInfo.Name}");
                // 剔除所有 0x00 字节，这些字节会导致 Postgres UTF8 报错
                byte[] cleanBytes = bytes.Where(b => b != 0).ToArray();
                File.WriteAllBytes(filePath, cleanBytes);
                fixedNfos++;
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[跳过] 无法访问 {Path.GetFileName(filePath)}: {ex.Message}");
    }
}

Console.WriteLine("--------------------------------------------");
Console.WriteLine($"任务完成！{count}个文件 修复文件名: {fixedFiles}，修复 NFO 内容: {fixedNfos}");