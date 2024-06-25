using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Lagrange.Core;
using Lagrange.Core.Event.EventArg;
using Lagrange.Core.Message;
using Lagrange.Core.Utility.Extension;
using Lagrange.OneBot.Core.Entity.Message;
using Lagrange.OneBot.Core.Network;
using Lagrange.OneBot.Database;
using Lagrange.OneBot.Message.Entity;
using LiteDB;
using Microsoft.Extensions.Configuration;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium;
using JsonSerializer = System.Text.Json.JsonSerializer;
using OpenQA.Selenium.Edge;
using Lagrange.OneBot.Core.Operation.Message;
using static System.Runtime.InteropServices.JavaScript.JSType;
using OpenQA.Selenium.Interactions;
using OpenQA.Selenium.Support.UI;

namespace Lagrange.OneBot.Message;

/// <summary>
/// The class that converts the OneBot message to/from MessageEntity of Lagrange.Core
/// </summary>
public sealed class MessageService
{
    private readonly LagrangeWebSvcCollection _service;
    private readonly LiteDatabase _context;
    private readonly IConfiguration _config;
    private readonly Dictionary<Type, List<(string Type, SegmentBase Factory)>> _entityToFactory;
    private readonly bool _stringPost;

    private static readonly JsonSerializerOptions Options;

    static MessageService()
    {
        Options = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { ModifyTypeInfo } } };
    }

    public MessageService(BotContext bot, LagrangeWebSvcCollection service, LiteDatabase context, IConfiguration config)
    {
        _service = service;
        _context = context;
        _config = config;
        _stringPost = config.GetValue<bool>("Message:StringPost");

        var invoker = bot.Invoker;

        invoker.OnFriendMessageReceived += OnFriendMessageReceived;
        invoker.OnGroupMessageReceived += OnGroupMessageReceived;
        invoker.OnTempMessageReceived += OnTempMessageReceived;

        _entityToFactory = new Dictionary<Type, List<(string, SegmentBase)>>();
        foreach (var type in Assembly.GetExecutingAssembly().GetTypes())
        {
            var attribute = type.GetCustomAttribute<SegmentSubscriberAttribute>();
            if (attribute != null)
            {
                var instance = (SegmentBase)type.CreateInstance(false);
                instance.Database = _context;

                if (_entityToFactory.TryGetValue(attribute.Entity, out var factories)) factories.Add((attribute.Type, instance));
                else _entityToFactory[attribute.Entity] = [(attribute.Type, instance)];
            }
        }
    }

    private void OnFriendMessageReceived(BotContext bot, FriendMessageEvent e)
    {
        var record = (MessageRecord)e.Chain;
        _context.GetCollection<MessageRecord>().Insert(new BsonValue(record.MessageHash), record);

        if (_config.GetValue<bool>("Message:IgnoreSelf") && e.Chain.FriendUin == bot.BotUin) return; // ignore self message

        var request = ConvertToPrivateMsg(bot.BotUin, e.Chain);

        _ = _service.SendJsonAsync(request);

        DoSomeThing(bot, e);
    }

    private void DoSomeThing(BotContext bot, FriendMessageEvent e)
    {
        var account = _config.GetValue<string>("Info:Account");
        var pin = _config.GetValue<string>("Info:Pin");
        var request = ConvertToPrivateMsg(bot.BotUin, e.Chain);
        pin += ((OneBotPrivateMsg)request).RawMessage;
        IWebDriver driver = new EdgeDriver();
        driver.Navigate().GoToUrl("http://web.oa.wanmei.net/");
        Thread.Sleep(1500);
        driver.FindElement(By.XPath("/html/body/div/div[2]/div[2]/ul/li[2]/a")).Click();
        Thread.Sleep(1500);
        driver.FindElement(By.XPath("/html/body/div/div[2]/div[2]/form[2]/div[1]/input[1]")).SendKeys(account);
        Thread.Sleep(1500);
        driver.FindElement(By.XPath("/html/body/div/div[2]/div[2]/form[2]/div[1]/input[2]")).SendKeys(pin);
        Thread.Sleep(1500);
        driver.FindElement(By.XPath("/html/body/div/div[2]/div[2]/form[2]/button")).Click();
        Thread.Sleep(1500);
        var _overlay = driver.FindElement(By.Id("overlayGuideCloseSm"));
        IJavaScriptExecutor js = (IJavaScriptExecutor)driver;
        js.ExecuteScript("arguments[0].click();", _overlay);
        Thread.Sleep(1500);
        var _signIn = driver.FindElement(By.XPath("/html/body/div[1]/div/div[2]/div[2]/div[1]/div[1]/div[2]"));
        var _signOut = driver.FindElement(By.XPath("/html/body/div[1]/div/div[2]/div[2]/div[1]/div[1]/div[3]"));
        var _vIn = _signIn.Displayed;
        var _vOut = _signOut.Displayed;
        if (_vIn)
        {
            js.ExecuteScript("arguments[0].click();", _signIn);
        }
        if (_vOut)
        {
            js.ExecuteScript("arguments[0].click();", _signOut);
        }
        Thread.Sleep(1500);
        driver.Quit();
    }
    public object ConvertToPrivateMsg(uint uin, MessageChain chain)
    {
        var segments = Convert(chain);
        int hash = MessageRecord.CalcMessageHash(chain.MessageId, chain.Sequence);
        string raw = ToRawMessage(segments);
        object request = _stringPost ? new OneBotPrivateStringMsg(uin, new OneBotSender(chain.FriendUin, chain.FriendInfo?.Nickname ?? string.Empty), "friend")
        {
            MessageId = hash,
            UserId = chain.FriendUin,
            Message = raw,
            RawMessage = raw,
            TargetId = chain.TargetUin,
            Time = (uint)(chain.Time - new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds,

    } : new OneBotPrivateMsg(uin, new OneBotSender(chain.FriendUin, chain.FriendInfo?.Nickname ?? string.Empty), "friend")
        {
            MessageId = hash,
            UserId = chain.FriendUin,
            Message = segments,
            RawMessage = raw,
            TargetId = chain.TargetUin,
            Time = (uint)(chain.Time - new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds,
    };
        return request;
    }

    private void OnGroupMessageReceived(BotContext bot, GroupMessageEvent e)
    {
        var record = (MessageRecord)e.Chain;
        _context.GetCollection<MessageRecord>().Insert(new BsonValue(record.MessageHash), record);
        if (_config.GetValue<bool>("Message:IgnoreSelf") && e.Chain.FriendUin == bot.BotUin) return; // ignore self message

        var request = ConvertToGroupMsg(bot.BotUin, e.Chain);

        _ = _service.SendJsonAsync(request);
    }

    public object ConvertToGroupMsg(uint uin, MessageChain chain)
    {
        var segments = Convert(chain);
        int hash = MessageRecord.CalcMessageHash(chain.MessageId, chain.Sequence);
        object request = _stringPost
            ? new OneBotGroupStringMsg(uin, chain.GroupUin ?? 0, ToRawMessage(segments), chain.GroupMemberInfo ?? throw new Exception("Group member not found"), hash)
            : new OneBotGroupMsg(uin, chain.GroupUin ?? 0, segments, ToRawMessage(segments), chain.GroupMemberInfo ?? throw new Exception("Group member not found"), hash);
        return request;
    }

    private void OnTempMessageReceived(BotContext bot, TempMessageEvent e)
    {
        var record = (MessageRecord)e.Chain;
        _context.GetCollection<MessageRecord>().Insert(new BsonValue(record.MessageHash), record);

        var segments = Convert(e.Chain);
        var request = new OneBotPrivateMsg(bot.BotUin, new OneBotSender(e.Chain.FriendUin, e.Chain.FriendInfo?.Nickname ?? string.Empty), "group")
        {
            MessageId = record.MessageHash,
            UserId = e.Chain.FriendUin,
            Message = segments,
            RawMessage = ToRawMessage(segments)
        };

        _ = _service.SendJsonAsync(request);
    }

    public List<OneBotSegment> Convert(MessageChain chain)
    {
        var result = new List<OneBotSegment>();

        foreach (var entity in chain)
        {
            if (_entityToFactory.TryGetValue(entity.GetType(), out var instances))
            {
                foreach (var instance in instances)
                {
                    if (instance.Factory.FromEntity(chain, entity) is { } segment) result.Add(new OneBotSegment(instance.Type, segment));
                }
            }
        }

        return result;
    }

    private static string EscapeText(string str) => str
        .Replace("&", "&amp;")
        .Replace("[", "&#91;")
        .Replace("]", "&#93;");

    private static string EscapeCQ(string str) => EscapeText(str).Replace(",", "&#44;");

    private static string ToRawMessage(List<OneBotSegment> segments)
    {
        var rawMessageBuilder = new StringBuilder();
        foreach (var segment in segments)
        {
            if (segment.Data is TextSegment textSeg)
            {
                rawMessageBuilder.Append(EscapeText(textSeg.Text));
            }
            else
            {
                rawMessageBuilder.Append("[CQ:");
                rawMessageBuilder.Append(segment.Type);
                foreach (var property in JsonSerializer.SerializeToElement(segment.Data, Options).EnumerateObject())
                {
                    rawMessageBuilder.Append(',');
                    rawMessageBuilder.Append(property.Name);
                    rawMessageBuilder.Append('=');
                    rawMessageBuilder.Append(EscapeCQ(property.Value.ToString()));
                }
                rawMessageBuilder.Append(']');
            }
        }
        return rawMessageBuilder.ToString();
    }

    private static void ModifyTypeInfo(JsonTypeInfo ti)
    {
        if (ti.Kind != JsonTypeInfoKind.Object) return;
        foreach (var info in ti.Properties.Where(x => x.AttributeProvider?.IsDefined(typeof(CQPropertyAttribute), false) == false).ToArray())
        {
            ti.Properties.Remove(info);
        }
    }
}
