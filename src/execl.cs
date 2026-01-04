#:package EPPlus@8.4.0

using System.Text.Json;
using System.Text.Json.Serialization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OfficeOpenXml;
using System.Collections.Generic;
using System.Text.RegularExpressions;

//Excel文件所在的地址
FileInfo file = new(@"C:\Users\hiyan\Downloads\c_machine_position.xlsx");
ExcelPackage.License.SetNonCommercialPersonal("My Name");//个人
using (ExcelPackage excelPackage = new(file))
{
    //指定需要读入的sheet名
    ExcelWorksheet excelWorksheet = excelPackage.Workbook.Worksheets["c_machine_position"];
    //比如读取第一行,第一列的值数据
    object a = excelWorksheet.Cells[1, 1].Value;
    //读取第一行,第二列的值为
    object b = excelWorksheet.Cells[1, 3].Value;
    //然后根据需要对a，b转为字符串，或者double，int等..
    var rows = excelWorksheet.Dimension.End.Row;
    List<string> line = [];


    Console.WriteLine(JsonSerializer.Serialize(ConvertToGroupDictionary(line)));
    //Console.WriteLine(JsonSerializer.Serialize(line));
}

/// <summary>
/// 将字符串数组转换为分组字典
/// </summary>
/// <param name="dataArray">输入数据数组</param>
/// <returns>分组字典</returns>
 Dictionary<string, Dictionary<string, string>> ConvertToGroupDictionary(List<string> dataArray)
{
    var result = new Dictionary<string, Dictionary<string, string>>();
    var regex = MyRegex();

    foreach (var item in dataArray)
    {
        var match = regex.Match(item);
        if (!match.Success) continue;

        var groupName = match.Groups["group"].Value;
        var key = match.Groups["key"].Value;
        var value = match.Groups["value"].Value;

        // 确保分组存在
        if (!result.ContainsKey(groupName))
        {
            result[groupName] = new Dictionary<string, string>();
        }

        // 添加键值对（重复键会自动覆盖）
        result[groupName][key] = value;
    }

    return result;
}

partial class Program
{
    [GeneratedRegex(@"^(?<group>.+?)_(?<key>\d+):(?<value>.+)$", RegexOptions.Compiled)]
    private static partial Regex MyRegex();
}