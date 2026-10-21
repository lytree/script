using Spectre.Console;

// ─────────────────────────────────────────────────────────────
// 本文件移植自 chunsou（Python）: https://github.com/Funsiooo/chunsou
// 对应原项目文件：modules/core/tip.py
// Copyright (C) Funsiooo    Licensed under GPL-3.0（见 LICENSE）
// ─────────────────────────────────────────────────────────────

namespace Chunsou;

/// <summary>
/// FOFA / Hunter 搜索语法速查，对应 modules/core/tip.py。
/// 用 Spectre.Console 表格输出，取代 Python 版的 tabulate。
/// </summary>
public static class SearchTips
{
    private static readonly (string Query, string Desc, string Note)[] FofaRows =
    [
        ("title=\"beijing\"", "从标题中搜索“北京”", "-"),
        ("header=\"elastic\"", "从 http 头中搜索“elastic”", "-"),
        ("body=\"网络空间测绘\"", "从 html 正文中搜索“网络空间测绘”", "-"),
        ("fid=\"sSXXGNUO2FefBTcCLIT/2Q==\"", "查找相同的网站指纹", "搜索网站类型资产"),
        ("domain=\"qq.com\"", "搜索根域名带有 qq.com 的网站", "-"),
        ("icp=\"京ICP证030173号\"", "查找备案号为该值的网站", "搜索网站类型资产"),
        ("js_name=\"js/jquery.js\"", "查找正文包含该 js 的资产", "搜索网站类型资产"),
        ("js_md5=\"82ac3f14327a8b7ba49baa208d4eaa15\"", "查找 js 源码匹配的资产", "-"),
        ("cname=\"ap21.inst.siteforce.com\"", "查找 cname 为该值的网站", "-"),
        ("cname_domain=\"siteforce.com\"", "查找 cname 包含该值的网站", "-"),
        ("cloud_name=\"Aliyundun\"", "通过云服务名称搜索资产", "-"),
        ("product=\"NGINX\"", "搜索此产品的资产", "个人版及以上可用"),
        ("category=\"服务\"", "搜索此产品分类的资产", "个人版及以上可用"),
        ("sdk_hash==\"Mkb4Ms4R96glv/T6TRzwPWh3UDatBqeF\"", "搜索使用此 sdk 的资产", "商业版及以上可用"),
        ("icon_hash=\"-247388890\"", "搜索使用此 icon 的资产", "-"),
        ("host=\".gov.cn\"", "从 url 中搜索该片段", "搜索要用 host 作为名称"),
        ("port=\"6379\"", "查找对应端口的资产", "-"),
        ("ip=\"1.1.1.1\"", "从 ip 中搜索包含该值的网站", "搜索要用 ip 作为名称"),
        ("ip=\"220.181.111.1/24\"", "查询该 C 网段资产", "-"),
        ("status_code=\"402\"", "查询服务器状态为该值的资产", "查询网站类型数据"),
        ("protocol=\"quic\"", "查询 quic 协议资产", "开启端口扫描时有效"),
        ("country=\"CN\"", "搜索指定国家（编码）的资产", "-"),
        ("region=\"Xinjiang Uyghur Autonomous Region\"", "搜索指定行政区的资产", "-"),
        ("city=\"Ürümqi\"", "搜索指定城市的资产", "-"),
        ("cert=\"baidu\"", "搜索证书中带有该值的资产", "-"),
        ("cert.subject=\"Oracle Corporation\"", "搜索证书持有者为该值的资产", "-"),
        ("cert.issuer=\"DigiCert\"", "搜索证书颁发者为该值的资产", "-"),
        ("cert.is_valid=true", "验证证书是否有效", "个人版及以上可用"),
        ("cert.is_match=true", "证书和域名是否匹配", "个人版及以上可用"),
        ("cert.is_expired=false", "证书是否过期", "个人版及以上可用"),
        ("jarm=\"2ad...83e81\"", "搜索 JARM 指纹", "-"),
        ("banner=\"users\" && protocol=\"ftp\"", "组合条件搜索", "-"),
        ("type=\"service\"", "搜索所有协议资产", "支持 subdomain 和 service"),
        ("os=\"centos\"", "搜索 CentOS 资产", "-"),
        ("server==\"Microsoft-IIS/10\"", "搜索 IIS 10 服务器", "-"),
        ("app=\"Microsoft-Exchange\"", "搜索 Microsoft-Exchange 设备", "-"),
        ("after=\"2017\" && before=\"2017-10-01\"", "时间范围段搜索", "-"),
        ("asn=\"19551\"", "搜索指定 asn 的资产", "-"),
        ("org=\"LLC Baxet\"", "搜索指定 org 的资产", "-"),
        ("base_protocol=\"udp\"", "搜索指定 udp 协议的资产", "-"),
        ("is_fraud=false", "排除仿冒/欺诈数据", "专业版及以上可用"),
        ("is_honeypot=false", "排除蜜罐数据", "专业版及以上可用"),
        ("is_ipv6=true", "搜索 ipv6 的资产", "只接受 true 和 false"),
        ("is_domain=true", "搜索域名的资产", "只接受 true 和 false"),
        ("is_cloud=true", "筛选使用了云服务的资产", "-"),
    ];

    private static readonly (string Query, string Desc, string Note)[] HunterRows =
    [
        ("ip=\"1.1.1.1\"", "搜索 IP 为该值的资产", ""),
        ("ip=\"220.181.111.0/24\"", "搜索该 C 网段资产", ""),
        ("ip.port=\"80\"", "搜索开放端口为该值的资产", ""),
        ("ip.country=\"CN\"", "搜索主机所在国为中国", ""),
        ("ip.province=\"江苏\"", "搜索主机在江苏省的资产", ""),
        ("ip.city=\"北京\"", "搜索主机所在城市为北京的资产", ""),
        ("ip.isp=\"电信\"", "搜索运营商为中国电信的资产", ""),
        ("ip.os=\"Windows\"", "搜索操作系统为 Windows 的资产", ""),
        ("ip.port_count>\"2\"", "搜索开放端口大于 2 的 IP", "支持 = > <"),
        ("domain=\"qianxin.com\"", "搜索域名包含该值的网站", ""),
        ("domain.suffix=\"qianxin.com\"", "搜索主域为该值的网站", ""),
        ("domain.dns_type=\"mx\"", "搜索关联 MX 记录的资产", "可查看枚举值"),
        ("domain.status=\"clientDeleteProhibited\"", "搜索该域名状态的网站", "可查看枚举值"),
        ("domain.whois_server=\"whois.markmonitor.com\"", "搜索 whois 服务器", ""),
        ("domain.name_server=\"ns1.qq.com\"", "搜索名称服务器", ""),
        ("header.server==\"Microsoft-IIS/10\"", "搜索 server 全名匹配的服务器", ""),
        ("header.content_length=\"691\"", "搜索 HTTP 消息主体大小", ""),
        ("header.status_code=\"402\"", "搜索返回该状态码的资产", ""),
        ("header=\"elastic\"", "搜索 HTTP 请求头中含该值的资产", ""),
        ("is_web=true", "搜索 web 资产", ""),
        ("web.title=\"北京\"", "从网站标题中搜索", ""),
        ("web.body=\"网络空间测绘\"", "搜索网站正文包含该值的资产", ""),
        ("after=\"2021-01-01\" && before=\"2021-12-31\"", "搜索该时间段资产", ""),
        ("web.similar=\"baidu.com:443\"", "查询特征相似的资产", ""),
        ("web.similar_icon==\"17262739310191283300\"", "查询 icon 相似的资产", ""),
        ("web.icon=\"22eeab765346f14faf564a4709f98548\"", "查询 icon 相同的资产", ""),
        ("web.similar_id=\"3322dfb483ea6fd250b29de488969b35\"", "查询与该网页相似的资产", ""),
        ("web.tag=\"登录页面\"", "查询包含该资产标签的资产", "可查看枚举值"),
        ("icp.number=\"京ICP备16020626号-8\"", "搜索 ICP 备案号", ""),
        ("icp.web_name=\"奇安信\"", "搜索 ICP 网站名", ""),
        ("icp.name=\"奇安信\"", "搜索 ICP 备案单位名", ""),
        ("icp.type=\"企业\"", "搜索 ICP 备案主体类型", ""),
        ("icp.industry=\"软件和信息技术服务业\"", "搜索 ICP 备案行业", "可查看枚举值"),
        ("protocol=\"http\"", "搜索协议为 http 的资产", ""),
        ("protocol.transport=\"udp\"", "搜索传输层协议为 udp 的资产", ""),
        ("protocol.banner=\"nginx\"", "查询端口响应中包含该值的资产", ""),
        ("app.name=\"小米 Router\"", "搜索标记为该名称的资产", ""),
        ("app.type=\"开发与运维\"", "查询包含该组件分类的资产", ""),
        ("app.vendor=\"PHP\"", "查询包含该组件厂商的资产", ""),
        ("app.version=\"1.8.1\"", "查询包含该组件版本的资产", ""),
        ("cert=\"baidu\"", "搜索证书中带有该值的资产", ""),
        ("cert.subject=\"qianxin.com\"", "搜索证书使用者包含该值", ""),
        ("cert.subject.suffix=\"qianxin.com\"", "搜索证书使用者为该值", ""),
        ("cert.subject_org=\"奇安信科技集团股份有限公司\"", "搜索证书使用者组织", ""),
        ("cert.issuer=\"Let's Encrypt Authority X3\"", "搜索证书颁发者", ""),
        ("cert.issuer_org=\"Let's Encrypt\"", "搜索证书颁发者组织", ""),
        ("cert.sha-1=\"be7605a3...\"", "搜索证书签名 sha1 哈希", ""),
        ("cert.sha-256=\"4e529a65...\"", "搜索证书签名 sha256 哈希", ""),
        ("cert.sha-md5=\"aeedfb3c...\"", "搜索证书签名 shamd5 哈希", ""),
        ("cert.serial_number=\"35351242533515273557482149369\"", "搜索证书序列号", ""),
        ("cert.is_expired=true", "搜索证书已过期的资产", ""),
        ("cert.is_trust=true", "搜索证书可信的资产", ""),
        ("vul.gev=\"GEV-2021-1075\"", "查询存在该专项漏洞的资产", ""),
        ("vul.cve=\"CVE-2021-2194\"", "查询存在该漏洞的资产", ""),
        ("vul.gev=\"GEV-2021-1075\" && vul.state=\"已修复\"", "查询已修复的该漏洞资产", ""),
        ("web.is_vul=true", "查询存在历史漏洞的资产", ""),
    ];

    public static void Show()
    {
        Console.WriteLine(Ansi.Info("FOFA / Hunter 空间测绘基础搜索语法如下，请留意账号权限限制"));
        Console.WriteLine();

        Render("FOFA", FofaRows);
        Console.WriteLine();
        Render("Hunter", HunterRows);
    }

    private static void Render(string title, (string Query, string Desc, string Note)[] rows)
    {
        var table = new Table()
            .Title($"[bold]{title}[/] 搜索语法")
            .Border(TableBorder.Rounded)
            .AddColumn("[grey]例句[/]")
            .AddColumn("[grey]用途说明[/]")
            .AddColumn("[grey]注[/]");

        foreach (var (q, d, n) in rows)
        {
            table.AddRow(
                $"[yellow]{q.EscapeMarkup()}[/]",
                d.EscapeMarkup(),
                string.IsNullOrEmpty(n) || n == "-" ? "[grey]-[/]" : $"[grey]{n.EscapeMarkup()}[/]");
        }

        AnsiConsole.Write(table);
    }
}
