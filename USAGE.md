# Soford ERP 使用说明

本系统用于导入商品、质检商品、调用 Alibaba OpenAPI，并完成商品创建、更新、库存价格同步和发布流程。

本地访问地址：

```text
http://localhost:5173/
```

## 1. 初始化配置

打开页面后先查看右上角状态：

- `API configured`：Alibaba 配置基本完整。
- `API incomplete`：缺少 AppKey、AppSecret、Token 或发布接口配置。

如果还没有授权 Token：

1. 在 Alibaba 授权页面完成卖家授权。
2. 拿到回调里的 `code`。
3. 粘贴到页面里的 `Authorization code`。
4. 点击 `Create Token`。
5. 后续 Token 快过期时点击 `Refresh Token`。

生产回调地址：

```text
https://erp.soford.cn/openapi/callback
```

## 2. 导入商品

点击顶部 `Import` 上传 `.xlsx` 或 `.csv` 商品表。

也可以把文件拖到页面里的导入区域。

点击 `Template` 可以下载 CSV 模板。

模板字段：

```text
Sku, Title, CategoryId, Currency, Price, MOQ, Stock,
LeadTimeDays, MainImageUrl, DetailImageUrls, Keywords,
Attributes, Description
```

导入后商品会出现在商品表格中。

## 3. 商品质检

勾选商品后可以执行：

- `Quality`：检查商品标题、类目、价格、MOQ、库存、图片、关键词、属性等。
- `Preflight`：发布前检查，不会真正调用 Alibaba 发布接口。

如果商品有阻断问题，会显示在 `Issues` 列。点击问题数量可以查看详情。

## 4. Alibaba 商品发布闭环

勾选一个或多个商品后，建议按顺序操作：

1. `Predict Category`
   根据标题、描述、图片预测 Alibaba 类目。

2. `Query Attributes`
   根据商品 `CategoryId` 查询类目属性要求。

3. `Create Listing`
   创建 Alibaba 商品。

4. `Update Listing`
   更新 Alibaba 商品信息。

5. `Query Status`
   查询远端商品状态。

6. `Sync Inventory`
   同步库存。

7. `Sync Price`
   同步价格。

8. `Batch Publish`
   批量发布选中商品。发布前建议先点 `Preflight`。

## 5. 图片功能

在 `Images` 区域：

- `Upload Image`：上传图片到 Alibaba 图片库。
- `Group List Payload`：快速填充图片分组查询参数，然后在 `API Workbench` 中点击 `Call API`。

上传结果会显示在 API 返回区域。

## 6. 视频功能

在 `Videos` 区域：

- `Query Videos`：填充视频列表查询参数。
- `Main Video Payload`：填充商品主视频关联参数。

填充后需要在 `API Workbench` 点击 `Call API` 执行。

## 7. API Workbench 高级调用

`API Workbench` 用来直接测试 Alibaba API。

使用方式：

1. 在 `API` 下拉框选择接口，例如：
   - `category.predict`
   - `product.get`
   - `product.update`
   - `product.search`
   - `photobank.group.list`
   - `video.query`

2. 在 `Payload JSON` 中填写参数。

3. 点击 `Call API`。

返回结果会显示在下方黑色代码区域。

## 8. 日志和任务

页面底部有两块：

- `Publish Jobs`：显示预检、导出、发布任务记录。
- `API Logs`：显示最近 Alibaba API 调用记录，包括接口、状态码、traceId。

## 9. CSV 备用导出

如果 Alibaba API 未配置完整，或发布接口无权限，可以点击：

```text
CSV Fallback
```

系统会导出商品 CSV，便于人工上传或备用处理。

## 10. 推荐操作流程

日常发布商品建议按这个顺序：

```text
导入商品
-> 勾选商品
-> Quality
-> Predict Category
-> 填好 CategoryId
-> Query Attributes
-> 补齐属性
-> Preflight
-> Upload Image
-> Create Listing / Batch Publish
-> Query Status
-> Sync Inventory / Sync Price
```

## 11. 注意事项

- 必须先完成 Alibaba 授权 Token，否则所有需要 token 的接口都会失败。
- `AppKey`、`AppSecret`、Token 等敏感信息应放在 `.env` 或服务器环境变量中，不要提交到代码仓库。
- 商品创建和更新接口对字段要求严格，如果 Alibaba 返回字段错误，需要根据返回结果继续调整商品 payload 映射。
- 容器部署时，`App_Data` 必须使用持久化卷，否则 token、日志和商品数据会在容器重建后丢失。
