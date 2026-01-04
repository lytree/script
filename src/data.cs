#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property PublishTrimmed=false
#:package MySqlConnector@2.5.0

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using MySqlConnector;

Random rnd = new();

string connStr = "Server=10.100.50.48;Database=dgm2000_1_2010;Uid=root;Pwd=test;";
try
{// 2. 使用 using 语句确保连接自动关闭和释放
    using (var connection = new MySqlConnection(connStr))
    {
        // 打开连接
        await connection.OpenAsync();
        while (true)
        {
            var dataTime = DateTime.Now;
            var time = GetTimeStampSeconds(dataTime);
            for (int i = 0; i < 80; i++)
            {
                string sql =
                $""""
                INSERT INTO `dgm2000_1_2010`.`d_25102910473881669_viborder` (`id`, `saveTime`, `saveTime_Com`, `dataId`, `jc`, `speed`, `vib_rms`, `vib_p`, `vib_pp`, `vib_vsx1`, `vib_vsx2`, `vib_vsx3`, `vib_vsx4`, `vib_vsx5`, `vib_vsx6`, `vib_vsx7`, `vib_vsx8`, `vib_vsx1_scale`, `vib_vsx2_scale`, `vib_vsx3_scale`, `vib_vsx4_scale`, `vib_vsx5_scale`, `vib_vsx6_scale`, `vib_vsx7_scale`, `vib_vsx8_scale`, `vib_avg`, `gap`, `vib_wave_len`, `pow_rms`, `vib_k`, `vib_pf`, `vib_cf`, `vib_sf`, `sv`, `temperature`, `temperature_rise`, `line_number`, `train_number`, `carriage_number`, `train_reference_speed`, `current_station`, `next_station`, `total_load`, `current_carriage_load`, `wheel_diameter`, `train_total_mileage`, `diagnosis`, `vib_wave`, `reserve`) VALUES ({i}, '{dataTime.ToString("yyyy-MM-dd hh:mm:ss")}', {time}, 2, 0, {Math.Round(rnd.Next(100, 300) * 1.0, 2)}, {Math.Round(rnd.Next(100, 300) * 0.01, 2)}, {Math.Round(rnd.Next(100, 300) * 0.01, 2)},{Math.Round(rnd.Next(100, 300) * 0.01, 2)}, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.039386, NULL, 110.925, {Math.Round(rnd.Next(100, 300) * 0.01, 2)}, {Math.Round(rnd.Next(100, 300) * 0.01, 2)}, {Math.Round(rnd.Next(100, 300) * 0.01, 2)}, -0.000000000014435, 142.946, {Math.Round(rnd.Next(100, 300)*0.1, 2)}, {Math.Round(rnd.Next(100, 300)*0.1, 2)}, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, NULL, NULL);
                INSERT INTO `dgm2000_1_2010`.`d_25102910473881669_evporder` (`id`, `saveTime`, `saveTime_Com`, `dataId`, `jc`, `speed`, `spm_rms`, `spm_p`, `spm_pp`, `vib_vsx1`, `vib_vsx2`, `vib_vsx3`, `vib_vsx4`, `vib_vsx5`, `vib_vsx6`, `vib_vsx7`, `vib_vsx8`, `vib_vsx1_scale`, `vib_vsx2_scale`, `vib_vsx3_scale`, `vib_vsx4_scale`, `vib_vsx5_scale`, `vib_vsx6_scale`, `vib_vsx7_scale`, `vib_vsx8_scale`, `spm_wave_len`, `spm_avg`, `gap`, `pow_rms`, `sv`, `sv0`, `sv1`, `sv2`, `sv3`, `sv4`, `sv5`, `sv6`, `sv7`, `sv8`, `sv10`, `sv11`, `sv12`, `sv13`, `sv14`, `sv15`, `sv16`, `sv17`, `sv18`, `sv20`, `sv21`, `sv22`, `sv23`, `sv24`, `sv25`, `sv26`, `sv27`, `sv28`, `sv30`, `sv31`, `sv32`, `sv33`, `sv34`, `sv35`, `sv36`, `sv37`, `sv38`, `sv40`, `sv41`, `sv42`, `sv43`, `sv44`, `sv45`, `sv46`, `sv47`, `sv48`, `sv50`, `sv51`, `sv52`, `sv53`, `sv54`, `sv55`, `sv56`, `sv57`, `sv58`, `sv60`, `sv61`, `sv62`, `sv63`, `sv64`, `sv65`, `sv66`, `sv67`, `sv68`, `sv70`, `sv71`, `sv72`, `sv73`, `sv74`, `sv75`, `sv76`, `sv77`, `sv78`, `sv80`, `sv81`, `sv82`, `sv83`, `sv84`, `sv85`, `sv86`, `sv87`, `sv88`, `sv90`, `sv91`, `sv92`, `sv93`, `sv94`, `sv95`, `sv96`, `sv97`, `sv98`, `part_name0`, `part_name1`, `part_name2`, `part_name3`, `part_name4`, `part_name5`, `part_name6`, `part_name7`, `part_name8`, `part_name9`, `temperature`, `temperature_rise`, `line_number`, `train_number`, `carriage_number`, `train_reference_speed`, `current_station`, `next_station`, `total_load`, `current_carriage_load`, `wheel_diameter`, `train_total_mileage`, `diagnosis`, `spm_wave`, `reserve`) VALUES ({i}, '{dataTime.ToString("yyyy-MM-dd hh:mm:ss")}', {time}, 2, 0, {Math.Round(rnd.Next(100, 300) * 1.0, 2)}, 24.4488, 39.6156, 75.0732, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 190, 36.068, 0.0398234, 7.74764, {Math.Round(rnd.Next(200, 300) * 0.1, 2)},{Math.Round(rnd.Next(200, 300) * 0.1, 2)}, {Math.Round(rnd.Next(200, 300) * 0.1, 2)}, {Math.Round(rnd.Next(200, 300) * 0.1, 2)}, {Math.Round(rnd.Next(200, 300) * 0.1, 2)}, {Math.Round(rnd.Next(200, 300) * 0.1, 2)}, {Math.Round(rnd.Next(200, 300) * 0.1, 2)}, {Math.Round(rnd.Next(200, 300) * 0.1, 2)}, {Math.Round(rnd.Next(200, 300) * 0.1, 2)}, {Math.Round(rnd.Next(200, 300) * 0.1, 2)}, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, '轴箱轴承', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, 0, -1, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, 0, 0x010100000000003F80000000000000000000003F8000000000000045960000420000000000000100000020000000060000008B78010180007FFFE8791342E8EA1542D0EF804160EE26C1A0BF3341A0576E413090B7C198D40DC230C5C3C130B59CC13011E1C130A0DEC14013FC40608661C160840EC1D0CCEE41D0E8964168231742307BF9C1A0CF5A4168761E42609435C13064E8C1E8791342407BC140D070AA41306BD2C1609435C160A45CC13036C6C1309E8BC1D008E541850440C5, NULL);

                """";
                using (var insertCommand = new MySqlCommand(sql, connection))
                {
                    int rowsAffected = await insertCommand.ExecuteNonQueryAsync();
                    Console.WriteLine($"成功插入 {rowsAffected} 行数据。");
                }
            }
            Thread.Sleep(TimeSpan.FromSeconds(10));
        }
    }
}
catch (Exception e)
{

}

long GetTimeStampSeconds(DateTime dt)
{
    DateTime dateStart = new DateTime(1970, 1, 1, 8, 0, 0);
    return Convert.ToInt64((dt - dateStart).TotalMilliseconds);
}