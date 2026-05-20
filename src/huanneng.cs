using System.Text.RegularExpressions;
using System.IO;
using System.Buffers.Binary;
using System.Text;

// foreach(var name in new DirectoryInfo(@"D:\ftp\data").GetFiles())
// {
//     var names = name.Name.Split('_')[1];
//     Console.Write(name.Name+" : ");
//     foreach( var s in getstr(names,5))
//     {
//     Console.Write(Unicode2String("\\"+s));
//     }
//     Console.WriteLine(" ");
// }

// AE4_u90a3u4ec1u005fu90a3u4ec1u0035u0033u53f7u98ceu673a


var name = "FB1_u5e72u6cb3u53e3u005fu0043u0033u002du0030u0031u0046u673au7ec4_MBBR2_2560Hz_1_1143_20260519090820";

var names = name.Split('_')[1];
Console.Write(name + " : ");
foreach (var s in getstr(names, 5))
{
    Console.Write(Unicode2String("\\" + s));
}
Console.WriteLine(" ");

/// <summary>
/// <summary>
/// 字符串转Unicode
/// </summary>
/// <param name="source">源字符串</param>
/// <returns>Unicode编码后的字符串</returns>
static string String2Unicode(string source)
{
    byte[] bytes = Encoding.Unicode.GetBytes(source);
    StringBuilder stringBuilder = new StringBuilder();
    for (int i = 0; i < bytes.Length; i += 2)
    {
        stringBuilder.AppendFormat("\\u{0}{1}", bytes[i + 1].ToString("x").PadLeft(2, '0'), bytes[i].ToString("x").PadLeft(2, '0'));
    }
    return stringBuilder.ToString();
}

/// <summary>
/// Unicode转字符串
/// </summary>
/// <param name="source">经过Unicode编码的字符串</param>
/// <returns>正常字符串</returns>
static string Unicode2String(string source)
{
    return new Regex(@"\\u([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled).Replace(
                 source, x => string.Empty + Convert.ToChar(Convert.ToUInt16(x.Result("$1"), 16)));
}

/// 按照长度拆分字符串
/// </summary>
/// <param name="strs"></param>
/// <param name="len"></param>
/// <returns></returns>
static string[] getstr(string strs, int len)
{
    double i = strs.Length;
    string[] myarray = new string[int.Parse(Math.Ceiling(i / len).ToString())];
    for (int j = 0; j < myarray.Length; j++)
    {
        len = len <= strs.Length ? len : strs.Length;
        myarray[j] = strs.Substring(0, len);
        strs = strs.Substring(len, strs.Length - len);
    }
    return myarray;
}