using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;


namespace Helper;

/// <summary>
/// 时间工具类
/// </summary>
public static partial class Helpers
{
    //压缩字节
    //1.创建压缩的数据流
    //2.设定compressStream为存放被压缩的文件流,并设定为压缩模式
    //3.将需要压缩的字节写到被压缩的文件流
    public static byte[] CompressBytes(byte[] bytes)
    {
        using (MemoryStream compressStream = new())
        {
            using (var zipStream = new DeflateStream(compressStream, CompressionMode.Compress))
                zipStream.Write(bytes, 0, bytes.Length);
            return compressStream.ToArray();
        }
    }
    //解压缩字节
    //1.创建被压缩的数据流
    //2.创建zipStream对象，并传入解压的文件流
    //3.创建目标流
    //4.zipStream拷贝到目标流
    //5.返回目标流输出字节
    public static byte[] Decompress(byte[] bytes)
    {
        try
        {
            //检查data头是否是zlib标准头
            int flag = bytes[0] + bytes[1];
            List<byte> new_data = [.. bytes];
            //121,276,338分别为zlib的标头的十进制
            if (flag == 121 || flag == 276 || flag == 338)
            {
                new_data.RemoveRange(0, 2);
                new_data.RemoveRange(new_data.Count() - 4, 4);
            }
            using (var compressStream = new MemoryStream([.. new_data]))
            {
                using (var zipStream = new DeflateStream(compressStream, CompressionMode.Decompress))
                {
                    using (var resultStream = new MemoryStream())
                    {
                        zipStream.CopyTo(resultStream);
                        return resultStream.ToArray();
                    }
                }
            }
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }
        return [];
    }
}