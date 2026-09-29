using System.Text.Json;

/// <summary>Per-product Alibaba operations. Each one persists the outcome on the product and never throws for API errors.</summary>
public sealed class ProductOperations(
    ProductRepository products,
    ProductQualityService quality,
    AlibabaClient alibaba,
    TimeProvider time)
{
    public async Task<OperationItemResult> PublishAsync(Guid productId)
    {
        var product = await products.GetAsync(productId);
        if (product is null)
        {
            return new(productId, "", false, "商品不存在或已删除。");
        }

        var issues = await quality.ApplyAsync(product);
        var blockers = issues.Where(x => x.Severity == QualitySeverity.Blocker).ToList();
        if (blockers.Count > 0)
        {
            await products.UpdateAsync(productId, p =>
            {
                p.QualityIssues = product.QualityIssues;
                p.LocalState = LocalState.Incomplete;
            });
            return new(productId, product.Sku, false, "质检未通过：" + string.Join("；", blockers.Select(x => x.Message.TrimEnd('。'))) + "。");
        }

        var isUpdate = !string.IsNullOrWhiteSpace(product.RemoteProductId);
        var previousState = product.PublishState;
        await products.UpdateAsync(productId, p =>
        {
            p.QualityIssues = product.QualityIssues;
            p.LocalState = LocalState.Ready;
            p.PublishState = PublishState.Publishing;
        });

        var result = isUpdate
            ? await alibaba.CallAsync("product.update", ListingMapper.BuildUpdatePayload(product))
            : await alibaba.CallAsync("product.create", ListingMapper.BuildCreatePayload(product));
        var action = isUpdate ? "update" : "create";

        if (!result.Success)
        {
            await products.UpdateAsync(productId, p =>
            {
                // A failed update leaves the existing Alibaba listing as it was.
                p.PublishState = isUpdate ? previousState : PublishState.Failed;
                p.RemoteStatusMessage = $"{(isUpdate ? "更新" : "发布")}失败：{result.Describe()}";
            });
            return new(productId, product.Sku, false, AlibabaErrors.Explain(result), product.RemoteProductId, action);
        }

        var remoteId = isUpdate ? product.RemoteProductId : ExtractProductId(result.Json);
        if (string.IsNullOrWhiteSpace(remoteId))
        {
            await products.UpdateAsync(productId, p =>
            {
                p.PublishState = PublishState.Failed;
                p.RemoteStatusMessage = "Alibaba 返回成功但没有商品 ID，请在 API 日志中查看原始响应。";
            });
            return new(productId, product.Sku, false, "Alibaba 返回成功但没有商品 ID。", null, action);
        }

        await products.UpdateAsync(productId, p =>
        {
            p.RemoteProductId = remoteId;
            p.PublishState = PublishState.Pending;
            p.RemoteStatus = "pending";
            p.RemoteStatusMessage = isUpdate ? "已提交更新，等待 Alibaba 审核。" : "已提交发布，等待 Alibaba 审核。";
            p.LastPublishedAt = time.GetUtcNow();
        });

        var status = await RefreshStatusAsync(productId);
        var message = (isUpdate ? "更新成功" : "发布成功") + $"，Alibaba 商品 ID {remoteId}" + (status.Success ? $"，当前状态：{status.Message}" : "");
        return new(productId, product.Sku, true, message, remoteId, action);
    }

    public async Task<OperationItemResult> RefreshStatusAsync(Guid productId)
    {
        var (product, error) = await LoadPublishedAsync(productId);
        if (product is null) return error!;

        var result = await alibaba.CallAsync("product.status", ListingMapper.BuildStatusPayload(product));
        if (!result.Success)
        {
            return new(productId, product.Sku, false, AlibabaErrors.Explain(result), product.RemoteProductId, "status");
        }

        var status = AlibabaResponseParser.ScalarText(AlibabaResponseParser.Find(result.Json!.Value, "status"))?.ToLowerInvariant() ?? "";
        var description = AlibabaResponseParser.ScalarText(AlibabaResponseParser.Find(result.Json!.Value, "status_desc"));
        var (state, label) = MapStatus(status, product.PublishState);
        await products.UpdateAsync(productId, p =>
        {
            p.RemoteStatus = status;
            p.PublishState = state;
            p.RemoteStatusMessage = string.IsNullOrWhiteSpace(description) ? label : $"{label}：{description}";
            p.LastSyncedAt = time.GetUtcNow();
        });
        return new(productId, product.Sku, true, string.IsNullOrWhiteSpace(description) ? label : $"{label}（{description}）", product.RemoteProductId, "status");
    }

    public async Task<OperationItemResult> SyncInventoryAsync(Guid productId)
    {
        var (product, error) = await LoadPublishedAsync(productId);
        if (product is null) return error!;

        var result = await alibaba.CallAsync("product.inventory.update", ListingMapper.BuildInventoryPayload(product));
        return await RecordSyncAsync(product, result, "inventory", $"库存已同步为 {product.Stock}");
    }

    public async Task<OperationItemResult> SyncPriceAsync(Guid productId)
    {
        var (product, error) = await LoadPublishedAsync(productId);
        if (product is null) return error!;

        if (!string.Equals(product.Currency, "USD", StringComparison.OrdinalIgnoreCase))
        {
            return new(productId, product.Sku, false, "Alibaba 改价接口只支持 USD，请把币种改为 USD 后再同步。", product.RemoteProductId, "price");
        }

        var tiers = string.Join("，", ListingMapper.EffectiveTiers(product).Select(x => $"≥{x.Quantity}: ${x.Price}"));
        var result = await alibaba.CallAsync("product.price.update", ListingMapper.BuildPricePayload(product));
        return await RecordSyncAsync(product, result, "price", $"价格已同步（{tiers}）");
    }

    public async Task<List<OperationItemResult>> SetOnlineAsync(IReadOnlyList<Guid> productIds, bool online)
    {
        var items = new List<OperationItemResult>();
        var eligible = new List<ProductRecord>();
        foreach (var id in productIds)
        {
            var (product, error) = await LoadPublishedAsync(id);
            if (product is null) items.Add(error!);
            else eligible.Add(product);
        }

        if (eligible.Count == 0) return items;

        var action = online ? "online" : "offline";
        var result = await alibaba.CallAsync("product.status.update", ListingMapper.BuildOnlineOfflinePayload(eligible, online));
        var updatedIds = ReadIdList(result.Json, "updated_product_ids");
        var errorById = ReadStatusErrors(result.Json);
        foreach (var product in eligible)
        {
            var ok = updatedIds.Contains(product.RemoteProductId!) || (result.Success && updatedIds.Count == 0 && errorById.Count == 0);
            if (ok)
            {
                await products.UpdateAsync(product.Id, p =>
                {
                    p.PublishState = online ? PublishState.Online : PublishState.Offline;
                    p.RemoteStatus = action;
                    p.RemoteStatusMessage = online ? "已上架" : "已下架";
                    p.LastSyncedAt = time.GetUtcNow();
                });
                items.Add(new(product.Id, product.Sku, true, online ? "已上架" : "已下架", product.RemoteProductId, action));
            }
            else
            {
                var reason = errorById.TryGetValue(product.RemoteProductId!, out var message) ? message : AlibabaErrors.Explain(result);
                items.Add(new(product.Id, product.Sku, false, reason, product.RemoteProductId, action));
            }
        }

        return items;
    }

    public async Task<OperationItemResult> PredictCategoryAsync(Guid productId)
    {
        var product = await products.GetAsync(productId);
        if (product is null) return new(productId, "", false, "商品不存在。");
        if (string.IsNullOrWhiteSpace(product.Title)) return new(productId, product.Sku, false, "请先填写商品标题。");

        var result = await alibaba.CallAsync("category.predict", new
        {
            title = product.Title,
            description = string.IsNullOrWhiteSpace(product.Description) ? null : AlibabaResponseParser.Truncate(product.Description, 1000),
            image = product.Images.FirstOrDefault()
        });
        if (!result.Success) return new(productId, product.Sku, false, AlibabaErrors.Explain(result), null, "predict");

        var categoryId = AlibabaResponseParser.ScalarText(AlibabaResponseParser.Find(result.Json!.Value, "category_id"));
        if (string.IsNullOrWhiteSpace(categoryId)) return new(productId, product.Sku, false, "Alibaba 未返回推荐类目。", null, "predict");

        var name = AlibabaResponseParser.ScalarText(AlibabaResponseParser.Find(result.Json!.Value, "category_name")) ?? "";
        var path = AlibabaResponseParser.ScalarText(AlibabaResponseParser.Find(result.Json!.Value, "category_path")) ?? "";
        var updated = await products.UpdateAsync(productId, p =>
        {
            p.CategoryId = categoryId;
            p.CategoryName = name;
            p.CategoryPath = path;
        });
        if (updated is not null)
        {
            await quality.ApplyAsync(updated);
            await products.UpdateAsync(productId, p =>
            {
                p.QualityIssues = updated.QualityIssues;
                p.LocalState = updated.LocalState;
            });
        }

        return new(productId, product.Sku, true, $"{categoryId} {(path.Length > 0 ? path : name)}", null, "predict");
    }

    private async Task<(ProductRecord? Product, OperationItemResult? Error)> LoadPublishedAsync(Guid productId)
    {
        var product = await products.GetAsync(productId);
        if (product is null) return (null, new(productId, "", false, "商品不存在。"));
        if (string.IsNullOrWhiteSpace(product.RemoteProductId) || !long.TryParse(product.RemoteProductId, out _))
        {
            return (null, new(productId, product.Sku, false, "该商品尚未发布到 Alibaba，请先发布。"));
        }

        return (product, null);
    }

    private async Task<OperationItemResult> RecordSyncAsync(ProductRecord product, AlibabaApiResult result, string action, string successMessage)
    {
        if (!result.Success) return new(product.Id, product.Sku, false, AlibabaErrors.Explain(result), product.RemoteProductId, action);

        await products.UpdateAsync(product.Id, p => p.LastSyncedAt = time.GetUtcNow());
        return new(product.Id, product.Sku, true, successMessage, product.RemoteProductId, action);
    }

    public static (PublishState State, string Label) MapStatus(string status, PublishState current) => status switch
    {
        "online" => (PublishState.Online, "已上架"),
        "pending" => (PublishState.Pending, "审核中"),
        "draft" => (PublishState.Pending, "Alibaba 草稿"),
        "failed" => (PublishState.Failed, "审核未通过"),
        "offline" => (PublishState.Offline, "已下架"),
        "" => (current, "未知状态"),
        _ => (current, status)
    };

    public static string? ExtractProductId(JsonElement? json)
    {
        if (json is null) return null;
        if (json.Value.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object
            && AlibabaResponseParser.ScalarText(result.TryGetProperty("data", out var data) ? data : null) is { Length: > 0 } fromResult)
        {
            return fromResult;
        }

        return AlibabaResponseParser.ScalarText(AlibabaResponseParser.Find(json.Value, "product_id"));
    }

    private static HashSet<string> ReadIdList(JsonElement? json, string name)
    {
        var set = new HashSet<string>();
        if (json is not null && AlibabaResponseParser.Find(json.Value, name) is { ValueKind: JsonValueKind.Array } list)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (AlibabaResponseParser.ScalarText(item) is { } id) set.Add(id);
            }
        }

        return set;
    }

    private static Dictionary<string, string> ReadStatusErrors(JsonElement? json)
    {
        var result = new Dictionary<string, string>();
        if (json is null || AlibabaResponseParser.Find(json.Value, "errors") is not { ValueKind: JsonValueKind.Array } errors) return result;

        foreach (var error in errors.EnumerateArray())
        {
            var id = error.TryGetProperty("product_id", out var productId) ? AlibabaResponseParser.ScalarText(productId) : null;
            if (id is null) continue;
            var messages = error.TryGetProperty("errors", out var details) && details.ValueKind == JsonValueKind.Array
                ? details.EnumerateArray().Select(x => $"{AlibabaResponseParser.Text(x, "msg_code")} {AlibabaResponseParser.Text(x, "message")}".Trim())
                : [];
            result[id] = string.Join("；", messages);
        }

        return result;
    }
}

/// <summary>Human-readable explanations for the Alibaba error codes operators meet most often.</summary>
public static class AlibabaErrors
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NotConfigured"] = "未配置 AppKey / AppSecret",
        ["NotAuthorized"] = "店铺未授权或授权已失效",
        ["IllegalAccessToken"] = "授权已失效，请在「店铺连接」重新授权",
        ["IncompleteSignature"] = "签名不正确，请检查 AppSecret",
        ["InvalidAppKey"] = "AppKey 无效",
        ["AppCallLimit"] = "调用频率超限，请稍后重试",
        ["InsufficientPermission"] = "应用没有该接口权限，请在 App Console 申请",
        ["InsufficientIsvPermissions"] = "应用没有该接口权限，请在 App Console 申请",
        ["NetworkError"] = "网络异常，无法连接 Alibaba",
        ["B_PRODUCT_PARAM_INVALID"] = "商品参数不合法",
        ["B_TITLE_NOT_FOUND"] = "缺少标题",
        ["B_PRICE_ALL_NULL"] = "价格为空",
        ["B_PRICE_SEQUENCE_INVALID"] = "阶梯价顺序不正确（数量递增、价格递减）",
        ["B_PRICE_SCALE_INVALID"] = "价格小数位超过两位",
        ["B_CONTAINS_INVALID_IMAGE"] = "图片链接无效",
        ["B_IMAGE_UPLOAD_FAILED"] = "图片全部上传失败，请检查图片链接是否可公开访问",
        ["B_KEYWORD_NOT_FOUND"] = "缺少关键词"
    };

    public static string Explain(AlibabaApiResult result)
    {
        if (result.Success) return "OK";
        var hint = result.ErrorCode is not null && Known.TryGetValue(result.ErrorCode, out var text) ? text : null;
        var detail = string.Join(" ", new[] { result.ErrorCode, result.ErrorMessage }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return hint is null ? detail : $"{hint}（{detail}）";
    }
}
