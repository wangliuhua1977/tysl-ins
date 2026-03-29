# 高德地图坐标转换接入说明（BD09→GCJ02）

## 1. 文档目的

本文档用于将当前项目中**已经验证可用**的高德地图坐标转换做法，提炼为一份可直接复用到**另一个封闭项目**的实施说明。

目标是解决以下问题：

- 平台返回的设备经纬度为 **BD-09**
- 高德地图前端展示要求使用 **GCJ-02**
- 需要一套稳定、易迁移、易排障的实现方式
- 希望封闭项目可以**直接照着做**，而不是重新摸索

---

## 2. 当前项目真实生效的做法

当前项目真实生效的主链路不是后台手写偏移公式，而是：

1. 后台把平台设备经纬度按 **BD-09 注册坐标**解析。
2. 如果原始坐标已经是 **GCJ-02**，则直接上图。
3. 如果原始坐标是 **BD-09**，后台不直接做数学转换，只给前端打上一个标记：`amap_js_convert_from_baidu`。
4. WPF 宿主会通过 `WebView2` 加载独立的高德坐标转换宿主页；宿主页收到点位后，逐点调用：

```javascript
AMap.convertFrom(points, "baidu", callback)
```

将 **BD-09** 转成 **GCJ-02**。
5. 转换成功后，宿主页会把逐点转换结果回传给 C#，并把成功结果写回本地渲染坐标缓存；地图宿主页优先使用已缓存的高德渲染坐标上图。
6. 某个点转换失败时，只会把该点标记为未上图，不会拖垮同批已成功点位。

---

## 3. 推荐给封闭项目的总体方案

建议封闭项目完全沿用这套思路：

### 3.1 后台职责

后台只负责：

- 解析平台返回的经纬度字符串
- 判断原始坐标是否合法
- 判断原始坐标属于什么坐标系
- 告诉前端“这个点是否需要高德前端转换”

后台**不建议**自己手写 BD-09 → GCJ-02 偏移算法。

### 3.2 前端职责

前端地图页负责：

- 对已经是 GCJ-02 的点直接上图
- 对 BD-09 的点调用高德官方 SDK 的 `AMap.convertFrom(..., "baidu", ...)`
- 转换完成后统一渲染 marker

### 3.3 为什么这样做

原因如下：

- 当前项目已经验证通过，风险最低
- 使用高德官方 SDK，稳定性高
- 代码职责清晰，后台和前端分工明确
- 日后排障时容易判断问题出在“原始坐标”“转换”“渲染”哪个环节

---

## 4. 后台数据结构设计

封闭项目不要只保留 `longitude` / `latitude` 两个字段。

必须明确区分：

- **RegisteredCoordinate**：平台原始登记坐标
- **MapCoordinate**：最终用于地图渲染的坐标
- **MapSource**：地图坐标来源

推荐使用以下模型：

```csharp
public enum CoordinateSystemKind
{
    Unknown,
    BD09,
    GCJ02
}

public enum PointCoordinateStatus
{
    Valid,
    Missing,
    Incomplete,
    Invalid,
    ZeroOrigin,
    ConversionFailed
}

public sealed record CoordinateValueModel(
    double Longitude,
    double Latitude,
    CoordinateSystemKind CoordinateSystem);

public sealed record PointCoordinateModel(
    string? RawLongitude,
    string? RawLatitude,
    CoordinateValueModel? RegisteredCoordinate,
    CoordinateValueModel? MapCoordinate,
    PointCoordinateStatus Status,
    bool CanRenderOnMap,
    string StatusText,
    string DiagnosticsText,
    string MapSource);
```

---

## 5. 后台坐标解析规则

后台在处理平台返回的经纬度时，必须统一执行以下规则。

### 5.1 都为空

如果经度和纬度都为空：

- `Status = Missing`
- `CanRenderOnMap = false`

### 5.2 半填

如果只填了经度或只填了纬度：

- `Status = Incomplete`
- `CanRenderOnMap = false`

### 5.3 格式错误

如果经纬度无法转成数字：

- `Status = Invalid`
- `CanRenderOnMap = false`

### 5.4 越界

如果经度不在 `[-180, 180]` 或纬度不在 `[-90, 90]`：

- `Status = Invalid`
- `CanRenderOnMap = false`

### 5.5 原点坐标

如果坐标是 `(0,0)`：

- `Status = ZeroOrigin`
- `CanRenderOnMap = false`

### 5.6 合法坐标

如果通过以上校验：

- 生成 `RegisteredCoordinate`
- `Status = Valid`

推荐解析代码如下：

```csharp
public static ParsedRegisteredCoordinateModel ParseRegistered(
    string? rawLongitude,
    string? rawLatitude,
    CoordinateSystemKind sourceSystem = CoordinateSystemKind.BD09)
{
    var longitudeText = Normalize(rawLongitude);
    var latitudeText = Normalize(rawLatitude);

    if (string.IsNullOrWhiteSpace(longitudeText) && string.IsNullOrWhiteSpace(latitudeText))
    {
        return new ParsedRegisteredCoordinateModel(
            longitudeText,
            latitudeText,
            null,
            PointCoordinateStatus.Missing,
            "未配置经纬度",
            "未配置经纬度");
    }

    if (string.IsNullOrWhiteSpace(longitudeText) || string.IsNullOrWhiteSpace(latitudeText))
    {
        return new ParsedRegisteredCoordinateModel(
            longitudeText,
            latitudeText,
            null,
            PointCoordinateStatus.Incomplete,
            "经纬度不完整",
            "经纬度不完整");
    }

    if (!double.TryParse(longitudeText, out var longitudeValue)
        || !double.TryParse(latitudeText, out var latitudeValue))
    {
        return new ParsedRegisteredCoordinateModel(
            longitudeText,
            latitudeText,
            null,
            PointCoordinateStatus.Invalid,
            "经纬度格式异常",
            "经纬度格式异常");
    }

    var coordinate = new CoordinateValueModel(longitudeValue, latitudeValue, sourceSystem);

    if (longitudeValue == 0d && latitudeValue == 0d)
    {
        return new ParsedRegisteredCoordinateModel(
            longitudeText,
            latitudeText,
            coordinate,
            PointCoordinateStatus.ZeroOrigin,
            "经纬度落在原点，暂不可落图",
            "经纬度落在原点，暂不可落图");
    }

    if (longitudeValue is < -180d or > 180d || latitudeValue is < -90d or > 90d)
    {
        return new ParsedRegisteredCoordinateModel(
            longitudeText,
            latitudeText,
            coordinate,
            PointCoordinateStatus.Invalid,
            "经纬度超出有效范围",
            "经纬度超出有效范围");
    }

    return new ParsedRegisteredCoordinateModel(
        longitudeText,
        latitudeText,
        coordinate,
        PointCoordinateStatus.Valid,
        "坐标可落点",
        "坐标可落点");
}
```

---

## 6. 后台坐标分流规则

后台在拿到合法坐标后，不要直接“一刀切改写坐标值”。

应该按坐标系分流。

### 6.1 原始坐标已经是 GCJ-02

这种情况直接可上图：

- `RegisteredCoordinate = GCJ02`
- `MapCoordinate = RegisteredCoordinate`
- `MapSource = "registered_gcj02"` 或 `"resolved_gcj02"`

### 6.2 原始坐标是 BD-09

这种情况不要直接转换：

- `RegisteredCoordinate = BD09`
- `MapCoordinate = null`
- `CanRenderOnMap = true`
- `MapSource = "amap_js_convert_from_baidu"`

推荐代码如下：

```csharp
public static PointCoordinateModel FromParsedRegistered(ParsedRegisteredCoordinateModel parsed)
{
    if (!parsed.IsValid || parsed.Coordinate is null)
    {
        return new PointCoordinateModel(
            parsed.RawLongitude,
            parsed.RawLatitude,
            parsed.Coordinate,
            null,
            parsed.Status,
            false,
            parsed.StatusText,
            parsed.DiagnosticsText,
            "unavailable");
    }

    return parsed.Coordinate.CoordinateSystem == CoordinateSystemKind.GCJ02
        ? CreateResolved(
            parsed.Coordinate,
            parsed.Coordinate,
            "原始注册坐标已是 GCJ-02，可直接上图。",
            parsed.RawLongitude,
            parsed.RawLatitude)
        : CreateClientConvertible(
            parsed.Coordinate,
            parsed.RawLongitude,
            parsed.RawLatitude);
}

public static PointCoordinateModel CreateClientConvertible(
    CoordinateValueModel registeredCoordinate,
    string? rawLongitude = null,
    string? rawLatitude = null)
{
    return new PointCoordinateModel(
        rawLongitude ?? registeredCoordinate.Longitude.ToString(CultureInfo.InvariantCulture),
        rawLatitude ?? registeredCoordinate.Latitude.ToString(CultureInfo.InvariantCulture),
        registeredCoordinate,
        null,
        PointCoordinateStatus.Valid,
        true,
        "坐标可落点",
        "原始注册坐标为 BD-09，将在地图前端通过 AMap.convertFrom(points, \"baidu\", ...) 转换后上图。",
        "amap_js_convert_from_baidu");
}
```

---

## 7. 前端地图页的分批处理逻辑

前端收到点位数组后，不要直接全部画 marker。

必须先按 `MapSource` 分成三类：

- **directRenderablePoints**：直接可渲染
- **baiduConvertiblePoints**：需要调用高德转换
- **skippedPoints**：异常点，跳过

推荐代码如下：

```javascript
function prepareRenderBatches(points) {
  const directRenderablePoints = [];
  const baiduConvertiblePoints = [];
  const skippedPoints = [];

  for (const point of points) {
    const mapLongitude = Number(point.mapLongitude);
    const mapLatitude = Number(point.mapLatitude);
    const registeredLongitude = Number(point.registeredLongitude);
    const registeredLatitude = Number(point.registeredLatitude);
    const source = point.mapSource || "unavailable";

    if ((source === "registered_gcj02" || source === "resolved_gcj02")
        && Number.isFinite(mapLongitude)
        && Number.isFinite(mapLatitude)) {
      directRenderablePoints.push({
        ...point,
        mapLongitude,
        mapLatitude,
        registeredLongitude,
        registeredLatitude
      });
      continue;
    }

    if (source === "amap_js_convert_from_baidu"
        && Number.isFinite(registeredLongitude)
        && Number.isFinite(registeredLatitude)) {
      baiduConvertiblePoints.push({
        ...point,
        registeredLongitude,
        registeredLatitude
      });
      continue;
    }

    skippedPoints.push(point);
  }

  return {
    directRenderablePoints,
    baiduConvertiblePoints,
    skippedPoints
  };
}
```

---

## 8. 前端高德转换的核心代码

这是封闭项目最关键的实现。

推荐直接使用高德官方 JS SDK，但要注意 `result.locations` 不是只会返回一种形态。

```javascript
function resolveConvertedLocation(result) {
  const location = Array.isArray(result?.locations)
    ? result.locations[0]
    : result?.locations || result;

  if (!location) {
    throw new Error("amap_result_missing");
  }

  if (typeof location === "string") {
    const firstPair = location.split(";")[0];
    const parts = firstPair.split(",");
    if (parts.length < 2) {
      throw new Error("amap_result_string_invalid");
    }

    return {
      lng: Number(parts[0]),
      lat: Number(parts[1]),
      resultLocationKind: "string"
    };
  }

  return {
    lng: typeof location.getLng === "function" ? Number(location.getLng()) : Number(location.lng ?? location[0]),
    lat: typeof location.getLat === "function" ? Number(location.getLat()) : Number(location.lat ?? location[1]),
    resultLocationKind: Array.isArray(location) ? "array" : "object"
  };
}

function convertSinglePoint(point) {
  return new Promise(resolve => {
    const lng = Number(point.registeredLongitude);
    const lat = Number(point.registeredLatitude);

    if (!Number.isFinite(lng) || !Number.isFinite(lat)) {
      resolve({
        ...point,
        hasMapCoordinate: false,
        coordinateState: "failed",
        coordinateStateText: "原始坐标格式非法",
        coordinateWarning: "高德 JS 输入坐标不是有效数字。",
        failureStage: "input-validate",
        failureReasonCode: "raw_coordinate_not_numeric"
      });
      return;
    }

    AMap.convertFrom([lng, lat], "baidu", (status, result) => {
      const info = typeof result?.info === "string" ? result.info : "";
      if (status !== "complete") {
        resolve({
          ...point,
          hasMapCoordinate: false,
          coordinateState: "failed",
          coordinateStateText: "高德坐标转换返回失败",
          coordinateWarning: `status=${status} / ${info}`,
          failureStage: "convert-from",
          failureReasonCode: "convert_from_failed",
          conversionStatus: status,
          conversionInfo: info
        });
        return;
      }

      try {
        const converted = resolveConvertedLocation(result);
        resolve({
          ...point,
          hasMapCoordinate: true,
          coordinateState: "available",
          coordinateStateText: "已获取并转换坐标",
          mapLongitude: converted.lng,
          mapLatitude: converted.lat,
          conversionStatus: status,
          conversionInfo: info,
          resultLocationKind: converted.resultLocationKind
        });
      } catch (error) {
        resolve({
          ...point,
          hasMapCoordinate: false,
          coordinateState: "failed",
          coordinateStateText: "高德回传值非法",
          coordinateWarning: error instanceof Error ? error.message : "amap_result_invalid",
          failureStage: "result-validate",
          failureReasonCode: "js_result_invalid",
          conversionStatus: status,
          conversionInfo: info
        });
      }
    });
  });
}
```

### 8.1 说明

- 输入：原始 BD-09 点数组
- 输出：转换后的 GCJ-02 点数组
- 成功时：逐点返回可渲染的 GCJ-02 坐标
- 失败时：返回逐点失败原因，不让整张地图崩掉，也不吞掉同批成功点位

---

## 9. 前端最终渲染流程

前端地图页整体流程建议如下：

### 第 1 步

收到后台点位数据。

### 第 2 步

调用：

```javascript
const batches = prepareRenderBatches(points);
```

### 第 3 步

- `directRenderablePoints` 直接用于渲染
- `baiduConvertiblePoints` 先调用 `convertBaiduBatch()`

### 第 4 步

合并最终可渲染点集：

```javascript
const finalRenderablePoints =
  batches.directRenderablePoints.concat(convertedRenderablePoints);
```

### 第 5 步

统一渲染 marker：

```javascript
syncMarkers(finalRenderablePoints);
```

### 9.1 推荐总控制代码

```javascript
function applyState(points) {
  const batches = prepareRenderBatches(points);

  const finalizeRender = convertedRenderablePoints => {
    const finalRenderablePoints =
      batches.directRenderablePoints.concat(convertedRenderablePoints);

    syncMarkers(finalRenderablePoints);
  };

  if (batches.baiduConvertiblePoints.length === 0) {
    finalizeRender([]);
    return;
  }

  convertBaiduBatch(batches.baiduConvertiblePoints).then(finalizeRender);
}
```

---

## 10. 高德地图 SDK 初始化方式

如果封闭项目采用 WebView2 + HTML 宿主页方式嵌入高德地图，可按以下流程加载。

### 10.1 设置安全配置

```javascript
window._AMapSecurityConfig = {
  securityJsCode: securityJsCode
};
```

### 10.2 动态加载 JS SDK

```javascript
function loadAmap(apiKey, securityJsCode, apiVersion = "2.0") {
  return new Promise((resolve, reject) => {
    if (window.AMap) {
      resolve();
      return;
    }

    window._AMapSecurityConfig = { securityJsCode };

    const script = document.createElement("script");
    script.src = `https://webapi.amap.com/maps?v=${encodeURIComponent(apiVersion)}&key=${encodeURIComponent(apiKey)}`;
    script.async = true;
    script.onload = () => window.AMap ? resolve() : reject(new Error("amap_runtime_missing"));
    script.onerror = () => reject(new Error("amap_script_failed"));
    document.head.appendChild(script);
  });
}
```

### 10.3 初始化地图

```javascript
function initMap() {
  map = new AMap.Map("map", {
    zoom: 11,
    center: [103.765, 29.552],
    viewMode: "3D",
    mapStyle: "amap://styles/normal"
  });
}
```

> 注：中心点请替换为你项目自己的默认中心。

---

## 11. 日志与排障建议

封闭项目必须补齐日志，否则后期地图点位异常时很难判断问题在哪。

### 11.1 后台建议记录

每个点至少记录：

- 设备编码
- 原始经度
- 原始纬度
- JS 输入是否合法
- `AMap.convertFrom` 的 `status`
- `result.info`
- 回传坐标值
- 回传坐标值形态（`string / object / array`）
- 失败阶段（脚本加载 / convertFrom / JS 结果校验 / C# 解析 / 地图消费）
- 是否最终上图

### 11.2 前端建议记录

每次渲染至少记录：

- 总点数
- 缓存命中点数
- 待高德转换点数
- 转换成功点数
- 平台未提供坐标点数
- 坐标获取限频点数
- 坐标转换或解析失败点数
- 地图最终实际消费点数

### 11.3 转换失败时

不要让地图整页异常退出。

正确做法是：

- 当前失败点跳过
- 正常点继续显示
- 在日志中写清楚失败阶段、失败原因和原始输入坐标

### 11.4 当前项目的统计口径

当前项目已统一使用：

- **未上图** = `missing + rate_limited + failed`
- **missing** = 平台未提供坐标
- **rate_limited** = 平台坐标获取限频，稍后重试
- **failed** = 高德脚本加载失败、`AMap.convertFrom` 返回失败、JS 回传值非法、C# 解析后字段不合法或地图未消费结果

不要再把“未定位”和“未上图”混成一个模糊概念。  
如果页面展示的是 `missing + rate_limited + failed` 的总数，文案应明确写成“未上图”。

---

## 12. 服务端高德转换的备用方案

如果封闭项目后续有需要，也可以增加服务端版高德坐标转换。

服务端方式调用的是高德 Web 服务接口：

```text
https://restapi.amap.com/v3/assistant/coordinate/convert
```

请求示例：

```csharp
var requestUrl =
    $"{ConvertEndpoint}?locations={Uri.EscapeDataString(locations)}&coordsys=baidu&output=json&key={Uri.EscapeDataString(_settings.AmapWebServiceApiKey)}";
```

### 12.1 适用场景

只有在以下情况下才建议启用服务端版：

- 希望后台提前把 BD-09 统一转成 GCJ-02
- 希望把转换结果写入数据库或缓存
- 前端不方便调用高德 JS SDK
- 导出 CSV 时需要直接导出 GCJ-02

### 12.2 当前推荐

如果封闭项目当前主要目标只是“地图点位正确显示”，仍建议优先采用：

**后台打标记 + 前端 `AMap.convertFrom` 转换**

因为这是当前项目已经验证通过的主链路，迁移成本最低。

---

## 13. 推荐实施顺序

### 第 1 轮：打通最小可用链路

- 新增后台坐标模型
- 后台实现坐标解析
- 后台实现 `MapSource` 分流
- 前端接入高德地图 SDK
- 前端实现 `prepareRenderBatches`
- 前端实现 `convertBaiduBatch`
- 前端实现 marker 渲染

### 第 2 轮：补日志

- 后台解析日志
- 前端渲染日志
- 转换失败日志

### 第 3 轮：补异常处理

- 转换失败不影响其他点
- 坐标异常点直接跳过
- 页面不抛出全局错误

### 第 4 轮：再考虑服务端转换

只有在确有业务需要时，再增加服务端高德坐标转换能力。

---

## 14. 交付给封闭项目的最终实现规则

可直接复制以下文字给封闭项目开发者：

```text
本项目地图坐标转换按以下规则实现：

1. 平台设备登记坐标默认为 BD-09。
2. 后台不直接手写 BD-09 -> GCJ-02 偏移公式。
3. 后台先解析原始经纬度，做空值、半填、格式异常、越界、0,0 原点校验。
4. 若原始坐标已是 GCJ-02，则直接作为地图坐标使用，mapSource=registered_gcj02 或 resolved_gcj02。
5. 若原始坐标为 BD-09，则后台仅保留 RegisteredCoordinate，不生成 MapCoordinate，mapSource=amap_js_convert_from_baidu。
6. 前端地图页在渲染前，将所有 mapSource=amap_js_convert_from_baidu 的点收集成批次，调用 AMap.convertFrom(points, "baidu", callback) 转为 GCJ-02。
7. 转换成功后，将返回的 lng/lat 写入 mapLongitude/mapLatitude，再与原本直接可渲染的 GCJ-02 点合并，统一渲染 marker。
8. 转换失败的点不影响整张地图，只跳过失败点，并记录逐点日志和失败分类。
9. 服务端高德 Web API 转换能力可作为备用方案，但当前首选实现仍为前端高德 JS SDK 转换。
```

---

## 15. 封闭项目迁移时的注意事项

### 15.1 坐标优先级不要写乱

如果项目以后还有“用户手工补录坐标”，建议优先级明确为：

- 用户补录坐标
- 平台返回坐标
- 无坐标

如果用户补录坐标直接就是高德地图使用坐标（GCJ-02），则应直接按 GCJ-02 上图，不再走 BD-09 转换。

### 15.2 MapSource 必须保留

不要把 `MapSource` 省略掉。  
否则前端无法知道：

- 这个点能不能直接上图
- 这个点需不需要先转换
- 这个点为什么被跳过

### 15.3 不要把异常点硬渲染

以下情况必须跳过：

- 经纬度缺失
- 经纬度半填
- 经纬度格式错误
- 越界
- 0,0 原点
- 转换失败

---

## 16. 最终建议

对于另一个封闭项目，最稳妥的落地方式就是：

**后台做校验和分流，前端使用高德官方 `AMap.convertFrom(..., "baidu", ...)` 完成 BD-09 → GCJ-02 转换，再统一落图。**

这是当前项目已经实际验证通过的做法，适合直接复用。
## 17. 2026-03-29 当前实现补充

- `CoordConvHost.html` 当前采用两段式调用：先 `initializeCoordConvHost(...)` 等待宿主页与高德 JS SDK ready，再由 `convertBd09Batch(...)` 执行批量转换。
- `convertBd09Batch(...)` 是当前唯一批量入口；所有成功、部分失败、全部失败、脚本失败和异常路径都会返回单一 envelope，不再依赖只发 `postMessage` 的副通道。
- C# 宿主当前会显式等待 `coord-conv-ready`，超时会记录 `CoordConv host ready timeout`，不会再把 `{}`、空字符串、`null` 或 `undefined` 误当成成功结果。
- 当前 BD-09 -> GCJ-02 主链路已按高德 JS SDK 约束拆批，每批最多 40 个点，并记录 `batchIndex`、`batchDeviceCodes`、`payloadSummary` 和逐点错误信息。
- `AMap.convertFrom` 的 `result.locations` 当前按 `string / array / object / LngLat(getLng,getLat)` 多形态兼容解析；单点失败不会拖垮整批成功点。
- 成功转换后的坐标会写回本地 `MapLatitude / MapLongitude`，并把来源收口到现有 `CoordinateSource = "amap_js_convert_from_baidu"`，供地图页后续直接复用。
