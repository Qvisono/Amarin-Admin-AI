using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;

internal static class Program
{
    private static readonly List<object> Results = [];
    private static object? _sink;
    private static string _root = "";

    private static void Main(string[] args)
    {
        _root = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(_root);
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ru-RU");
        foreach (var spec in new[] {
            (Name: "text-120messages", Turns:60, Images:0, Bytes:0),
            (Name: "text-1200messages", Turns:600, Images:0, Bytes:0),
            (Name: "images-4x512KiB", Turns:60, Images:4, Bytes:512*1024),
            (Name: "images-4x2MiB", Turns:60, Images:4, Bytes:2*1024*1024)
        })
        {
            var session = Session(spec.Turns, spec.Images, spec.Bytes);
            var inline = JsonSerializer.Serialize(session, AppJson.Options);
            var external = ChatAttachmentFiles.Externalize(inline, out var blobs);
            var bySha = blobs.ToDictionary(blob => blob.Sha, blob => blob.Bytes);
            var expanded = ChatAttachmentFiles.Inline(external, sha => bySha.GetValueOrDefault(sha));
            var profile = Path.Combine(_root, spec.Name);
            var chats = Path.Combine(profile, "chats");
            Directory.CreateDirectory(chats);
            File.WriteAllText(Path.Combine(chats, "probe.json"), external, Encoding.UTF8);
            var folder = ChatAttachmentFiles.FolderOf(chats, "probe");
            Directory.CreateDirectory(folder);
            foreach (var blob in blobs)
                File.WriteAllBytes(ChatAttachmentFiles.BlobPath(folder, blob.Sha), blob.Bytes);

            var refs = 0;
            ChatAttachmentFiles.Inline(external, sha => { refs++; return bySha.GetValueOrDefault(sha); });
            Results.Add(new { Case=spec.Name, Metadata=true, Messages=session.Messages.Count, InlineJsonChars=inline.Length,
                ExternalJsonChars=external.Length, BlobCount=blobs.Count, ReferenceReads=refs, UniqueBytes=blobs.Sum(blob=>(long)blob.Bytes.Length) });
            var store = new ChatStore(profile);
            var restored = store.TryLoad("probe") ?? throw new Exception("Чат не загрузился");
            if (restored.Messages.Count != spec.Turns*2 || restored.Messages.Sum(message=>message.Images.Count) != spec.Images)
                throw new Exception("Неверный результат загрузки");
            Measure(spec.Name, "ReadMainJson", () => File.ReadAllText(Path.Combine(chats,"probe.json")));
            Measure(spec.Name, "InlineFromMemory", () => ChatAttachmentFiles.Inline(external, sha => bySha.GetValueOrDefault(sha)));
            Measure(spec.Name, "InlineWithBlobCache", () => {
                var cache = new Dictionary<string,byte[]?>();
                return ChatAttachmentFiles.Inline(external, sha => {
                    if (!cache.TryGetValue(sha,out var value))
                        cache[sha] = value = ChatStore.ReadBlob(folder,sha);
                    return value;
                });
            });
            Measure(spec.Name, "DeserializeExpandedJson", () => JsonSerializer.Deserialize<ChatSession>(expanded, AppJson.Options));
            Measure(spec.Name, "TryLoad", () => store.TryLoad("probe"));
        }

        foreach(var count in new[] {10_000,100_000})
        {
            var random = new Random(42);
            var table = new TableData(["Category","Amount","Value"], Enumerable.Range(0,count)
                .Select(i => (IReadOnlyList<string>)new[]{ "Group"+(i%100).ToString(CultureInfo.InvariantCulture),
                    (random.NextDouble()*100000).ToString("F2",CultureInfo.InvariantCulture),
                    i.ToString(CultureInfo.InvariantCulture) }).ToList());
            var sort = new TableRequest([],[],[],[],[new TableSort("Amount",true)],100,false);
            var group = new TableRequest([],["Category"],[new TableAggregate("Amount","sum"),new TableAggregate("Amount","avg"),new TableAggregate("Amount","max")],[],[],100,true);
            Measure("table-"+count,"NumericSort",()=>TableQuery.Run(table,sort));
            var current=TableQuery.Run(table,sort);
            var typed=TypedSort(table);
            if (JsonSerializer.Serialize(current) != JsonSerializer.Serialize(typed))
            {
                throw new Exception("Эталон сортировки дал другой результат");
            }
            Measure("table-"+count,"TypedSortReference",()=>TypedSort(table));
            Measure("table-"+count,"Group3Aggregates",()=>TableQuery.Run(table,group));
        }

        foreach (var size in new[] { 1024*1024, 24*1024*1024 })
        {
            var path=Path.Combine(_root,"synthetic-"+size+".log");
            var line="Synthetic local log entry; no user data; index=00000042; status=ok.\n";
            var text=string.Concat(Enumerable.Repeat(line,size/line.Length+1))[..size];
            File.WriteAllText(path,text,new UTF8Encoding(false));
            Measure("text-file-"+size,"Read100Lines",()=>TextFileReader.Read(DocumentSource.File(path),new DocumentWindow(Limit:100)));
        }

        // Только постоянная строка: замеряем запуск процесса, системных действий здесь нет.
        try
        {
          Measure("process","PowerShellConstant",()=>{
            var result = PowerShellHelper.RunAsync("Write-Output 'amarin-probe-ok'", 15).GetAwaiter().GetResult();
            if (!result.Success || !result.Output.Contains("amarin-probe-ok",StringComparison.Ordinal))
                throw new Exception("Подставная команда не выполнилась: "+result.Output);
            return result.Output;
          });
        }
        catch(Exception ex)
        {
            Results.Add(new {Case="process",Operation="PowerShellConstant",Unavailable=ex.Message});
        }

        File.WriteAllText(Path.Combine(_root,"results.json"),JsonSerializer.Serialize(Results,new JsonSerializerOptions{WriteIndented=true}));
        GC.KeepAlive(_sink);
    }

    private static TableResult TypedSort(TableData table)
    {
        var rows=table.Rows.Select(row=> (Row:row,Value:TableQuery.TryNumber(row[1],out var number)?number:double.NaN))
            .OrderByDescending(item=>item.Value).Take(100).Select(item=>item.Row).ToList();
        return new TableResult(table.Header,rows,table.Rows.Count,table.Rows.Count,table.Rows.Count,false,null);
    }

    private static void Measure(string scenario,string operation,Func<object?> action)
    {
        _sink = action();
        var samples = new List<(double Ms,long Alloc)>();
        for(var i=0;i<5;i++)
        {
            _sink=null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var before=GC.GetTotalAllocatedBytes(precise:true);
            var watch=Stopwatch.StartNew();
            _sink=action();
            watch.Stop();
            samples.Add((watch.Elapsed.TotalMilliseconds,GC.GetTotalAllocatedBytes(precise:true)-before));
        }
        var ordered=samples.OrderBy(sample=>sample.Ms).ToList();
        var record=new { Case=scenario,Operation=operation,MedianMs=ordered[2].Ms,
            MinMs=ordered[0].Ms,MaxMs=ordered[^1].Ms,MedianAllocatedMiB=ordered[2].Alloc/1048576.0,
            Samples=samples.Select(sample=>new { sample.Ms,sample.Alloc }).ToList() };
        Results.Add(record);
        Console.WriteLine(JsonSerializer.Serialize(record));
    }

    private static ChatSession Session(int turns,int images,int imageBytes)
    {
        var session = new ChatSession { Id="probe",Title="Синтетический чат",CreatedAt=new DateTime(2026,11,7),UpdatedAt=new DateTime(2026,11,7) };
        var paragraph="Проверка загрузки переписки и восстановления вложений. Команды и описания повторяются только в синтетическом наборе. ";
        var answer="### Проверка\n\n"+string.Concat(Enumerable.Repeat(paragraph,12))+"\n\n```csharp\nvar value = 42;\n```\n";
        var random = new Random(7);
        for(var i=0;i<turns;i++)
        {
            var user = new ChatDisplayMessage { Role="user",Id="u"+i,Text="Вопрос "+i };
            if(i<images)
            {
                var bytes=new byte[imageBytes];
                random.NextBytes(bytes);
                user.Images.Add(new ImageAttachment(Convert.ToBase64String(bytes),"image/png","synthetic.bin"));
            }
            session.Messages.Add(user);
            session.Messages.Add(new ChatDisplayMessage {Role="assistant",Id="a"+i,Text=answer});
            session.ApiMessages.Add(new ChatMessage {Role="user",Content=user.Images.Count>0?ChatContent.Multipart(user.Text,user.Images,null):ChatContent.Text(user.Text)});
            session.ApiMessages.Add(new ChatMessage {Role="assistant",Content=ChatContent.Text(answer)});
        }
        return session;
    }
}

