#:package Refit@9.0.2
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property PublishTrimmed=false
#:property Imports=$(RepoRoot)src/Helpers/Json.cs
using System.Text.Json;
using Refit;



IRealTimeControllerApi api = RestService.For<IRealTimeControllerApi>(new HttpClient()
{
    BaseAddress = new Uri("http://10.100.50.48:8085")
});


// var data = await api.getAllTrainStatus(1761706036647L);
// var data1 = await api.getAllLocStatus("25102910473881669");
// var data2 = await api.getLocStatus(new()
// {
//     {"lineId",1761706036647L},
//     {"trainId","25102910473881669"},
//     {"carriageId","25102910495831559"},
//     {"posLoc",""},
//     {"posClass",""},
//     {"startTime",1765345578000L},
//     {"endTime",1766109114000L},
// });
// var data3 = await api.getLocTempChat(new()
// {
//     {"lineId",1761706036647L},
//     {"trainId","25102910473881669"},
//     {"carriageId","25102910495831559"},
//     {"posLoc","1"},
//     {"posClass","1"},
//     {"startTime",1765345578000L},
//     {"endTime",1766109114000L},
// });

// var data = await api.getCarriageAlarmByTrain(1761706036647L, "25102910473881669");
// var data = await api.getCarriageStatus("25102910473881669");
// var data = await api.getTimeBatch(new()
// {
//     {"lineId",1761706036647L},
//     {"trainId","25102910473881669"},
//     {"startTime",1749085361000L},
//     {"endTime",1749087053001L},
// });

// var data = await api.getCorrugationTendencyV2(new()
// {
//     {"lineId",1761706036647L},
//     {"trainId","25102910473881669"},
//     {"startTime",1749085361000L},
//     {"endTime",1749087053001L},
//     {"direction",1},
//      {"fromSta",25},
//      {"toSta",1},
//     {"positionId1",new { positionId="0"}},
//     { "positionId2",new  { positionId="72" }}
// });


// var data = await api.getTrendDataBy(new()
// {
//     {"lineId",1761706036647L},
//     {"trainId","25102910473881669"},
//     {"carriageId","25102910495826242"},
//     {"startTime",1766367210000},
//     {"endTime",1766540010000L},
//     {"posLoc",1},
//     {"posClass",1},
//     {"dataType",9},

// });
var data = await api.getCorrugationInfo(new()
{
    {"lineId",1761706036647L},
    {"trainId","25102910473881669"},
    {"startTime",1749085361000L},
    {"endTime",1749087053001L},
    {"direction",0},
});
// var data = await api.listCarriageAlarmByTrain(1761706036647L, "25102910473881669");

Console.WriteLine(JsonSerializer.Serialize(data, Helpers.Helper.JsonOptions));

public interface IRealTimeControllerApi
{
    [Get("/tms/v1/realtime/lineStatus")]
    Task<dynamic> getAllTrainStatus(long lineId);



    [Get("/tms/v1/realtime/trainStatus")]
    Task<dynamic> getAllLocStatus(string trainId);

    [Get("/tms/v1/realtime/carriageStatus")]
    Task<dynamic> getCarriageStatus(string trainId);

    [Post("/tms/v1/realtime/locStatus")]
    Task<dynamic> getLocStatus(Dictionary<string, object> dir);


    [Post("/tms/v1/realtime/locTempData")]
    Task<dynamic> getLocTempChat(Dictionary<string, object> dir);



    [Get("/tms/v2/realAlarm/getCarriageAlarmByTrain")]
    Task<dynamic> getCarriageAlarmByTrain(long lineId, string trainId);
    [Post("/tms/v1/data/getTimeBatch")]
    Task<dynamic> getTimeBatch(Dictionary<string, object> dir);

    [Post("/tms/v2/data/getCorrugationTendency")]
    Task<dynamic> getCorrugationTendencyV2(Dictionary<string, object> dir);


    [Post("/tms/v1/data/getTrendDataBy")]
    Task<dynamic> getTrendDataBy(Dictionary<string, object> dir);


    [Get("/tms/v2/realAlarm/listCarriageAlarmByTrain")]
    Task<dynamic> listCarriageAlarmByTrain(long lineId, string trainId);

    [Post("/tms/v1/data/getCorrugationInfo")]
    Task<dynamic> getCorrugationInfo(Dictionary<string, object> dir);
}



