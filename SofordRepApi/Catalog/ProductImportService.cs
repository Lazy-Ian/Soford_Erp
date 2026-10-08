using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClosedXML.Excel;

/// <summary>Present holds the canonical fields that had a column in the sheet; only those update existing products.</summary>
/// <param name="RemoteProductId">Alibaba product ID from the sheet; when given it identifies the product instead of the SKU.</param>
public sealed record ImportRow(int RowNumber, ProductDraft Draft, IReadOnlySet<string> Present, string? RemoteProductId = null);

public sealed record ImportParseResult(List<ImportRow> Rows, List<string> Warnings, string[] Headers, string[] UnknownHeaders, int Skipped);

public sealed record ImportResult(int Created, int Updated, int Skipped, List<string> Warnings, string[] Headers, string[] UnknownHeaders);

public sealed partial class ProductImportService
{
    public const int MaxImportRows = 20_000;
    public const int MaxImportColumns = 100;
    private const long MaxSpreadsheetUncompressedBytes = 100L * 1024 * 1024;
    private const int MaxSpreadsheetEntries = 2_000;
    /// <summary>Canonical field → accepted header aliases. Headers are normalized (lower case, no spaces/punctuation, parentheses removed).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Fields = new Dictionary<string, string[]>
    {
        ["RemoteProductId"] = ["remoteproductid", "alibabaproductid", "alibabaid", "alibaba商品id", "商品id", "productid"],
        ["Sku"] = ["sku", "productcode", "itemcode", "商品编码", "货号", "编码"],
        ["Title"] = ["title", "producttitle", "englishtitle", "name", "productname", "标题", "英文标题", "商品标题", "商品名称"],
        ["Description"] = ["description", "productdescription", "details", "描述", "商品描述", "详情"],
        ["Keywords"] = ["keywords", "keyword", "关键词"],
        ["BrandName"] = ["brand", "brandname", "品牌"],
        ["ModelNumber"] = ["model", "modelnumber", "modelno", "型号"],
        ["Language"] = ["language", "语言"],
        ["CategoryId"] = ["categoryid", "category", "类目id", "类目"],
        ["CategoryName"] = ["categoryname", "类目名称"],
        ["Attributes"] = ["attributes", "属性"],
        ["Currency"] = ["currency", "币种"],
        ["Price"] = ["price", "unitprice", "价格", "单价"],
        ["TieredPrices"] = ["tieredprices", "tieredprice", "阶梯价"],
        ["MOQ"] = ["moq", "minimumorderquantity", "minorder", "起订量", "最小起订量"],
        ["Unit"] = ["unit", "单位", "销售单位"],
        ["Stock"] = ["stock", "inventory", "库存"],
        ["LeadTimeDays"] = ["leadtimedays", "leadtime", "交期", "交期天数", "发货期"],
        ["ShippingTemplateId"] = ["shippingtemplateid", "shippingtemplate", "运费模板", "运费模板id"],
        ["WeightKg"] = ["weightkg", "weight", "重量"],
        ["LengthCm"] = ["lengthcm", "length", "长", "长度"],
        ["WidthCm"] = ["widthcm", "width", "宽", "宽度"],
        ["HeightCm"] = ["heightcm", "height", "高", "高度"],
        ["Images"] = ["images", "imageurls", "图片"],
        ["MainImageUrl"] = ["mainimageurl", "mainimage", "主图"],
        ["DetailImageUrls"] = ["detailimageurls", "detailimages", "详情图"]
    };

    /// <summary>Template columns in order, with a Chinese explanation and an example value.</summary>
    public static readonly (string Header, bool Required, string Help, string Example)[] TemplateColumns =
    [
        ("RemoteProductId", false, "已在 Alibaba 上的商品填 Alibaba 商品 ID：按它匹配并更新，SKU 可留空。导出的 CSV 自带这一列", ""),
        ("Sku", true, "商品编码，唯一。没有 Alibaba 商品 ID 时按它匹配，已存在则更新", "SF-SPK-001"),
        ("Title", true, "英文标题，≤128 字符", "Portable Wireless Bluetooth Speaker Waterproof IPX7 Outdoor"),
        ("Description", true, "商品描述，支持 HTML", "20W stereo sound, 12h battery, IPX7 waterproof."),
        ("Keywords", false, "关键词，用 ; 分隔，最多 10 个", "bluetooth speaker;portable speaker;waterproof speaker"),
        ("BrandName", false, "品牌", "Soford"),
        ("ModelNumber", false, "型号", "SPK-001"),
        ("Language", false, "语言，默认 en_US", "en_US"),
        ("CategoryId", false, "Alibaba 叶子类目 ID，留空可在系统中预测", "201896803"),
        ("CategoryName", false, "类目名称（仅作参考）", "Speakers"),
        ("Attributes", false, "属性，格式 名称:值;名称:值", "Material:ABS;Color:Black;Place of Origin:China"),
        ("Currency", false, "币种，默认 USD", "USD"),
        ("Price", true, "单价（无阶梯价时必填）", "12.5"),
        ("TieredPrices", false, "阶梯价，格式 数量:价格;数量:价格（数量递增、价格递减）", "100:12.5;500:11.8"),
        ("MOQ", true, "起订量", "100"),
        ("Unit", false, "销售单位，默认 Piece", "Piece"),
        ("Stock", false, "库存", "5000"),
        ("LeadTimeDays", false, "交期（天）", "15"),
        ("ShippingTemplateId", false, "运费模板 ID", ""),
        ("WeightKg", false, "毛重（kg）", "0.6"),
        ("LengthCm", false, "包装长（cm）", "20"),
        ("WidthCm", false, "包装宽（cm）", "10"),
        ("HeightCm", false, "包装高（cm）", "10"),
        ("Images", true, "图片 URL，用 ; 分隔，第一张为主图，最多 6 张", "https://example.com/main.jpg;https://example.com/2.jpg")
    ];

    private static readonly Dictionary<string, string> AliasLookup = Fields
        .SelectMany(field => field.Value.Select(alias => (alias, field.Key)))
        .ToDictionary(x => x.alias, x => x.Key);

    static ProductImportService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public ImportParseResult Parse(Stream stream, string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var table = extension switch
        {
            ".xlsx" => ReadXlsx(stream),
            ".csv" => ReadCsv(stream),
            _ => throw new ImportFormatException("只支持 .xlsx 和 .csv 文件。")
        };

        return ParseTable(table);
    }

    public static ImportParseResult ParseTable(List<(int RowNumber, string[] Cells)> table)
    {
        if (table.Count == 0)
        {
            throw new ImportFormatException("文件为空。");
        }

        var headers = table[0].Cells.Select(x => x.Trim()).ToArray();
        var columns = new Dictionary<string, int>();
        var unknown = new List<string>();
        for (var i = 0; i < headers.Length; i++)
        {
            if (headers[i].Length == 0) continue;
            if (AliasLookup.TryGetValue(Normalize(headers[i]), out var field)) columns.TryAdd(field, i);
            else unknown.Add(headers[i]);
        }

        if (!columns.ContainsKey("Sku") && !columns.ContainsKey("RemoteProductId"))
        {
            throw new ImportFormatException("缺少 SKU 列或 Alibaba 商品 ID 列（表头应为 Sku / 商品编码，或 RemoteProductId / Alibaba 商品 ID）。请使用系统模板。");
        }

        var present = columns.Keys.ToHashSet();
        var rows = new List<ImportRow>();
        var warnings = new List<string>();
        var skipped = 0;
        foreach (var (rowNumber, cells) in table.Skip(1))
        {
            if (cells.All(string.IsNullOrWhiteSpace)) continue;

            string Get(string field) => columns.TryGetValue(field, out var index) && index < cells.Length ? cells[index].Trim() : "";

            var sku = Get("Sku");
            var remoteId = Get("RemoteProductId");
            if (sku.Length == 0 && remoteId.Length == 0)
            {
                warnings.Add($"第 {rowNumber} 行：SKU 和 Alibaba 商品 ID 都为空，已跳过。");
                skipped++;
                continue;
            }

            var rowWarnings = new List<string>();
            decimal Dec(string field, decimal fallback = 0)
            {
                var text = Get(field);
                if (text.Length == 0) return fallback;
                if (TryParseDecimal(text, out var value)) return value;
                rowWarnings.Add($"{field}「{text}」不是有效数字");
                return fallback;
            }

            decimal? OptionalDec(string field) => Get(field).Length == 0 ? null : Dec(field);
            int Int(string field, int fallback = 0)
            {
                var value = Math.Round(Dec(field, fallback));
                if (value is >= int.MinValue and <= int.MaxValue) return (int)value;
                rowWarnings.Add($"{field}「{Get(field)}」超出范围");
                return fallback;
            }

            var images = SplitList(Get("Images"));
            if (images.Length == 0)
            {
                images = new[] { Get("MainImageUrl") }.Concat(SplitList(Get("DetailImageUrls"))).Where(x => x.Length > 0).ToArray();
            }

            var tiers = ParseTiers(Get("TieredPrices"), rowWarnings);
            var draft = new ProductDraft(
                sku,
                Get("Title"),
                Get("Description"),
                SplitList(Get("Keywords")),
                Get("BrandName"),
                Get("ModelNumber"),
                Get("Language"),
                Get("CategoryId"),
                Get("CategoryName"),
                "",
                ParseAttributes(Get("Attributes"), rowWarnings),
                Get("Currency"),
                Dec("Price"),
                tiers,
                Int("MOQ", 1),
                Get("Unit"),
                Int("Stock"),
                Int("LeadTimeDays"),
                Get("ShippingTemplateId"),
                OptionalDec("WeightKg"),
                OptionalDec("LengthCm"),
                OptionalDec("WidthCm"),
                OptionalDec("HeightCm"),
                images,
                false);

            rows.Add(new ImportRow(rowNumber, draft, present, remoteId.Length > 0 ? remoteId : null));
            if (rowWarnings.Count > 0)
            {
                warnings.Add($"第 {rowNumber} 行（{(sku.Length > 0 ? sku : remoteId)}）：{string.Join("；", rowWarnings)}。");
            }
        }

        return new ImportParseResult(rows, warnings, headers, unknown.ToArray(), skipped);
    }

    public static string Normalize(string header)
    {
        var withoutParentheses = Parentheses().Replace(header, "");
        return Separators().Replace(withoutParentheses, "").ToLowerInvariant();
    }

    private static bool TryParseDecimal(string text, out decimal value)
    {
        var cleaned = text.Replace("$", "").Replace("¥", "").Replace("￥", "").Replace("US", "").Trim();
        return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    private static string[] SplitList(string value) =>
        value.Split([';', '；', '\n', '\r', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(part => part.StartsWith("http", StringComparison.OrdinalIgnoreCase) || !part.Contains(',') ? [part] : part.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToArray();

    private static List<PriceTier> ParseTiers(string value, List<string> warnings)
    {
        var tiers = new List<PriceTier>();
        foreach (var part in value.Split([';', '；', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split([':', '：', '='], 2, StringSplitOptions.TrimEntries);
            if (pair.Length == 2 && int.TryParse(pair[0], out var quantity) && TryParseDecimal(pair[1], out var price))
            {
                tiers.Add(new PriceTier(quantity, price));
            }
            else
            {
                warnings.Add($"阶梯价「{part}」格式应为 数量:价格");
            }
        }

        return tiers;
    }

    private static Dictionary<string, string> ParseAttributes(string value, List<string> warnings)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value)) return result;

        if (value.TrimStart().StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(value);
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    result[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? "" : property.Value.GetRawText();
                }

                return result;
            }
            catch (JsonException)
            {
                warnings.Add("属性 JSON 格式错误");
                return result;
            }
        }

        foreach (var part in value.Split([';', '；', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split([':', '：', '='], 2, StringSplitOptions.TrimEntries);
            if (pair.Length == 2 && pair[0].Length > 0 && pair[1].Length > 0) result[pair[0]] = pair[1];
            else warnings.Add($"属性「{part}」格式应为 名称:值");
        }

        return result;
    }

    private static List<(int, string[])> ReadXlsx(Stream stream)
    {
        ValidateXlsxArchive(stream);
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheets.First();
        var headerRow = sheet.FirstRowUsed();
        if (headerRow is null) return [];

        var lastColumn = headerRow.LastCellUsed()?.Address.ColumnNumber ?? 0;
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 0;
        if (lastColumn > MaxImportColumns) throw new ImportFormatException($"表格列数不能超过 {MaxImportColumns} 列。");
        if (lastRow - headerRow.RowNumber() > MaxImportRows) throw new ImportFormatException($"一次最多导入 {MaxImportRows} 行商品。");
        var table = new List<(int, string[])>();
        for (var rowNumber = headerRow.RowNumber(); rowNumber <= lastRow; rowNumber++)
        {
            var row = sheet.Row(rowNumber);
            table.Add((rowNumber, Enumerable.Range(1, lastColumn).Select(col => CellText(row.Cell(col))).ToArray()));
        }

        return table;
    }

    private static void ValidateXlsxArchive(Stream stream)
    {
        if (!stream.CanSeek) throw new ImportFormatException("无法读取不可定位的 XLSX 文件流。");
        stream.Position = 0;
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
        {
            if (archive.Entries.Count > MaxSpreadsheetEntries
                || archive.Entries.Sum(entry => entry.Length) > MaxSpreadsheetUncompressedBytes)
            {
                throw new ImportFormatException("XLSX 解压后的内容过大，已拒绝读取。");
            }
        }

        stream.Position = 0;
    }

    private static string CellText(IXLCell cell)
    {
        if (cell.DataType == XLDataType.Number)
        {
            return cell.GetDouble().ToString(CultureInfo.InvariantCulture);
        }

        return cell.GetFormattedString();
    }

    /// <summary>RFC 4180 CSV reader (quoted fields may contain commas, quotes and line breaks). Detects UTF-8 vs GBK.</summary>
    private static List<(int, string[])> ReadCsv(Stream stream)
    {
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();
        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            text = Encoding.GetEncoding("GB18030").GetString(bytes);
        }

        text = text.TrimStart('﻿');
        var rows = new List<(int, string[])>();
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var line = 1;
        var rowStart = 1;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { current.Append('"'); i++; }
                else if (c == '"') inQuotes = false;
                else { if (c == '\n') line++; current.Append(c); }
                continue;
            }

            switch (c)
            {
                case '"': inQuotes = true; break;
                case ',': fields.Add(current.ToString()); current.Clear(); break;
                case '\r': break;
                case '\n':
                    fields.Add(current.ToString()); current.Clear();
                    if (fields.Count > MaxImportColumns) throw new ImportFormatException($"CSV 列数不能超过 {MaxImportColumns} 列。");
                    rows.Add((rowStart, fields.ToArray())); fields.Clear();
                    if (rows.Count > MaxImportRows + 1) throw new ImportFormatException($"一次最多导入 {MaxImportRows} 行商品。");
                    line++; rowStart = line;
                    break;
                default: current.Append(c); break;
            }
        }

        if (current.Length > 0 || fields.Count > 0)
        {
            fields.Add(current.ToString());
            if (fields.Count > MaxImportColumns) throw new ImportFormatException($"CSV 列数不能超过 {MaxImportColumns} 列。");
            rows.Add((rowStart, fields.ToArray()));
            if (rows.Count > MaxImportRows + 1) throw new ImportFormatException($"一次最多导入 {MaxImportRows} 行商品。");
        }

        return rows;
    }

    public static byte[] BuildTemplate()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Products");
        for (var i = 0; i < TemplateColumns.Length; i++)
        {
            var column = TemplateColumns[i];
            var cell = sheet.Cell(1, i + 1);
            cell.Value = column.Header;
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = column.Required ? XLColor.FromHtml("#FDE2B8") : XLColor.FromHtml("#E8EEF7");
            cell.CreateComment().AddText($"{(column.Required ? "【必填】" : "")}{column.Help}");
        }

        sheet.SheetView.FreezeRows(1);
        sheet.Columns().Width = 18;

        var help = workbook.AddWorksheet("填写说明");
        help.Cell(1, 1).Value = "列名";
        help.Cell(1, 2).Value = "必填";
        help.Cell(1, 3).Value = "说明";
        help.Cell(1, 4).Value = "示例";
        help.Row(1).Style.Font.Bold = true;
        for (var i = 0; i < TemplateColumns.Length; i++)
        {
            var column = TemplateColumns[i];
            help.Cell(i + 2, 1).Value = column.Header;
            help.Cell(i + 2, 2).Value = column.Required ? "是" : "";
            help.Cell(i + 2, 3).Value = column.Help;
            help.Cell(i + 2, 4).Value = column.Example;
        }

        help.Cell(TemplateColumns.Length + 3, 1).Value = "说明：第一个工作表「Products」用于导入；按 SKU 匹配，已存在的商品会被更新（保留 Alibaba 发布状态）。表头也支持中文别名，如 商品编码、标题、价格、起订量、库存、交期、图片。";
        help.Cell(TemplateColumns.Length + 4, 1).Value = "只更新部分字段时，只保留 Sku 和需要修改的列即可（例如 Sku + Stock 只改库存）；表中存在但留空的列会把该字段清空。";
        help.Columns().AdjustToContents(1, 60);

        using var output = new MemoryStream();
        workbook.SaveAs(output);
        return output.ToArray();
    }

    [GeneratedRegex(@"[\(（\[].*?[\)）\]]")]
    private static partial Regex Parentheses();

    [GeneratedRegex(@"[\s_\-\*\.:：/]+")]
    private static partial Regex Separators();
}

public sealed class ImportFormatException(string message) : Exception(message);

public sealed class ExportService
{
    public byte[] BuildCsv(IEnumerable<ProductRecord> products)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(',', ProductImportService.TemplateColumns.Select(x => x.Header).Append("RemoteProductId").Append("PublishState")));
        foreach (var item in products)
        {
            var values = new[]
            {
                item.Sku, item.Title, item.Description, string.Join(';', item.Keywords), item.BrandName, item.ModelNumber,
                item.Language, item.CategoryId, item.CategoryName, string.Join(';', item.Attributes.Select(x => $"{x.Key}:{x.Value}")), item.Currency,
                Number(item.Price), string.Join(';', item.TieredPrices.Select(x => $"{x.Quantity}:{Number(x.Price)}")),
                item.MinimumOrderQuantity.ToString(CultureInfo.InvariantCulture), item.Unit, item.Stock.ToString(CultureInfo.InvariantCulture),
                item.LeadTimeDays.ToString(CultureInfo.InvariantCulture), item.ShippingTemplateId, Number(item.WeightKg), Number(item.LengthCm),
                Number(item.WidthCm), Number(item.HeightCm), string.Join(';', item.Images), item.RemoteProductId ?? "", item.PublishState.ToString()
            };
            builder.AppendLine(string.Join(',', values.Select(Escape)));
        }

        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(builder.ToString())).ToArray();
    }

    private static string Number(decimal? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";

    private static string Escape(string value)
    {
        var safe = value ?? "";
        // Prevent spreadsheet formula injection from product text.
        if (safe.Length > 0 && "=+-@".Contains(safe[0]) && !decimal.TryParse(safe, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
        {
            safe = "'" + safe;
        }

        return safe.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{safe.Replace("\"", "\"\"")}\"" : safe;
    }
}
