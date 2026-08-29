// 获取当前 .exe 所在目录
string baseDir = @"H:\Pictures\Rioko凉凉子";
Console.WriteLine($"[开始整理] 目录: {baseDir}\n");

int successCount = 0;

// 1. 获取所有一级子目录
var directories = Directory.GetDirectories(baseDir);

foreach (var dirPath in directories)
{
    try
    {
        var currentDir = new DirectoryInfo(dirPath);

        // 检查筛选条件：无文件 且 只有一个子文件夹
        var subDirs = currentDir.GetDirectories();
        var files = currentDir.GetFiles();

        if (files.Length == 0 && subDirs.Length == 1)
        {
            DirectoryInfo innerDir = subDirs[0];

            // 目标路径：即当前目录的同级位置
            // 例如：Parent/Middle/Inner -> Parent/Inner
            string targetPath = Path.Combine(baseDir, innerDir.Name);

            if (Directory.Exists(targetPath))
            {
                Console.WriteLine($"[合并] 目标目录 '{innerDir.Name}' 已存在，正在执行覆盖/合并...");

                // 执行合并逻辑
                MergeDirectories(innerDir.FullName, targetPath);

                // 合并完成后，删除已经空的原始子目录
                Directory.Delete(innerDir.FullName, true);
            }
            else
            {
                Console.WriteLine($"[移动] {innerDir.Name} <--- 从 {currentDir.Name} 上移");
                Directory.Move(innerDir.FullName, targetPath);
            }
            successCount++;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[错误] 处理 {dirPath} 时出错: {ex.Message}");
    }
}

Console.WriteLine($"\n整理完成！成功上移了 {successCount} 个文件夹。");
Console.WriteLine("按任意键退出...");
Console.ReadKey();



/// <summary>
/// 手动合并文件夹的方法
/// </summary>
static void MergeDirectories(string sourceDir, string destDir)
{
    // 移动所有文件
    foreach (var file in Directory.GetFiles(sourceDir))
    {
        string destFile = Path.Combine(destDir, Path.GetFileName(file));
        // 如果目标文件已存在，先删除再移动（实现覆盖）
        if (File.Exists(destFile)) File.Delete(destFile);
        File.Move(file, destFile);
    }

    // 递归处理子文件夹
    foreach (var subDir in Directory.GetDirectories(sourceDir))
    {
        string destSubDir = Path.Combine(destDir, Path.GetFileName(subDir));
        if (!Directory.Exists(destSubDir)) Directory.CreateDirectory(destSubDir);
        MergeDirectories(subDir, destSubDir);
    }
}