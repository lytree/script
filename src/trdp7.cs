#:package EPPlus@8.4.0

using OfficeOpenXml;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;


// 创建 options 实例
var options = new JsonSerializerOptions
{
    // 显式地将解析器设置为默认的（基于反射的）解析器
    TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    // 您可以添加其他选项，如 Indented = true 等
};

ExcelPackage.License.SetNonCommercialPersonal("lytree");

Dictionary<string, (string, string)> _locationMap =
       new()
       {
        // ======================= 轴 1 =======================
        {"1位轴端", ("1", "1")},
        {"2位轴端", ("1", "7")},
        {"1轴齿轮箱", ("1", "4")},
        {"1轴电机", ("1", "2")},
        
        // ======================= 轴 2 =======================
        {"3位轴端", ("2", "7")},
        {"4位轴端", ("2", "1")},
        {"2轴齿轮箱", ("2", "4")},
        {"2轴电机", ("2", "2")},
        
        // ======================= 轴 3 =======================
        {"5位轴端", ("3", "1")},
        {"6位轴端", ("3", "7")},
        {"3轴齿轮箱", ("3", "4")},
        {"3轴电机", ("3", "2")},
        
        // ======================= 轴 4 =======================
        {"7位轴端", ("4", "7")},
        {"8位轴端", ("4", "1")},
        {"4轴齿轮箱", ("4", "4")},
        {"4轴电机", ("4", "2")}
   };

using (ExcelPackage excelPackage = new(new FileInfo(@"D:\BaiduNetdiskDownload\trdp7.xlsx")))
{
    //指定需要读入的sheet名   
    ExcelWorksheet excelWorksheet = excelPackage.Workbook.Worksheets[0];

    //比如读取第一行,第一列的值数据 表头

    // object train_coach = excelWorksheet.Cells[1, 3].Value;
    // object signal_name = excelWorksheet.Cells[1, 5].Value;
    // object data_group = excelWorksheet.Cells[1, 6].Value;
    // object signal_code = excelWorksheet.Cells[1, 7].Value;
    // Console.WriteLine(train_coach);
    // Console.WriteLine(signal_name);
    // Console.WriteLine(data_group);
    // Console.WriteLine(signal_code);

    // int train_coach_index = 3;
    int signal_name_index = 3;
    int data_group_index = 1;
    int signal_code_index = 2;
    Dictionary<string, Dictionary<string, string>> dir = [];
    var rows = excelWorksheet.Dimension.End.Row;
    for (int i = 1; i <= rows; i++)
    {

        // var train_coach = excelWorksheet.Cells[i, train_coach_index].Value.ToString();
        var signal_name = excelWorksheet.Cells[i, signal_name_index].Value.ToString();
        var data_group = excelWorksheet.Cells[i, data_group_index].Value?.ToString();
        var signal_code = excelWorksheet.Cells[i, signal_code_index].Value.ToString();

        if (signal_name.Contains("预留"))
        {
            continue;
        }
        if (string.IsNullOrWhiteSpace(data_group))
        {
            continue;
        }
        var carriage = data_group.Replace("车数据", "");
        var type = "1";
        var charType = "";
        if (signal_name.Contains("复合传感器温度"))
        {
            type = "0";
        }
        if (signal_name.Contains("报警") || signal_name.Contains("预警"))
        {
            type = "2";
        }
        if (signal_name.Contains("踏面报警") || signal_name.Contains("踏面预警"))
        {
            charType = "28";
        }
        if (signal_name.Contains("冲击报警") || signal_name.Contains("冲击预警"))
        {
            charType = "19";
        }
        if (signal_name.Contains("温度报警"))
        {
            charType = "29";
        }
        var level = "";
        if (signal_name.Contains("预警"))
        {
            level = "1";
        }
        if (signal_name.Contains("一级"))
        {
            level = "2";
        }
        if (signal_name.Contains("二级"))
        {
            level = "3";
        }
        var loc = MatchLocation(signal_name);
        if (loc == null)
        {
            continue;
        }
        dir[signal_code] = new()
        {
            //车厢编号
            {"carriage" ,carriage},
            //车辆类型 Tc1 Tc2 Mp1 Mp2 M1 M2
            {"carriageType" , carriage},
            // 0 数据 , 1 状态，2 报警
            { "type" ,type},
            {"charType" , charType},
            {"level" , level},
            //部件分类
            {"posLoc" , loc?.Item2},
            //安装位置
            {"posClass" , loc?.Item1}
        };

    }

    Console.WriteLine(JsonSerializer.Serialize(dir, options));
    Console.WriteLine(dir.Count);


}




/// <summary>
/// 从报警消息中提取匹配的位置代码和轴编号。
/// </summary>
/// <param name="alarmMessage">报警描述字符串，例如 "7位轴端轴承二级冲击报警"</param>
/// <returns>匹配到的 (AxisId, LocationCode) 元组，如果未找到则返回 null。</returns>
(string, string)? MatchLocation(string alarmMessage)
{
    // 查找算法：遍历字典中的所有位置描述，检查报警消息是否包含该描述。

    foreach (var kvp in _locationMap)
    {
        string locationDescription = kvp.Key; // 例如: "7位轴端"
        (string, string) codes = kvp.Value;

        // 使用 String.Contains() 进行子字符串匹配
        if (alarmMessage.Contains(locationDescription))
        {
            // 找到第一个匹配项后立即返回
            Console.WriteLine($"[Debug] 找到匹配位置: {locationDescription}");
            return codes;
        }
    }

    // 如果遍历结束仍未找到匹配项
    return null;
}