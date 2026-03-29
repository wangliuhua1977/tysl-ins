# 监控目录树与目录层级设备同步说明（当前有效版）

## 1. 文档目的

本文档用于明确“监控目录 / 业务树”口径下的真实平台接口组合、当前仓库实现边界、SQLite 落地方式、UI 展示口径和最小对账范围。

如果项目目标是：

- 拉取监控目录树
- 递归拉取所有子目录
- 拉取每个目录层级下的全部设备
- 将目录树和设备完整落地到本地 SQLite
- 在 UI 中展示“真实监控目录树 + 所有目录层级设备”

则当前主链路必须使用本文档描述的接口组合，而不是继续把自定义分组接口当成监控目录树主链路。

## 2. 当前固定接口组合

当前监控目录树主链路固定为：

1. `getReginWithGroupList`
2. `getDeviceList`
3. `getCusDeviceCount`

其中：

- `getReginWithGroupList` 用于拉取当前层级下级目录。
- `getDeviceList` 用于拉取当前目录层级设备。
- `getCusDeviceCount` 仅用于最小对账，不是目录树主拉取接口。

以下接口不得再作为“监控目录树 / 全量点位设备”的主链路：

- `getGroupList`
- `getGroupDeviceList`
- `getGroupInfo`

## 3. 调用与加解密硬约束

当前链路继续遵循以下固定规则：

- 协议：`HTTPS + POST + application/x-www-form-urlencoded`
- 请求头必须带：`apiVersion=2.0`
- 请求公共参数固定为：`appId`、`clientType`、`params`、`timestamp`、`version`、`signature`
- 请求 `params` 始终使用 XXTea
- 响应只处理 `data` 字段
- 当前 `version=1.1` 时，响应 `data` 固定使用 RSA 私钥解密
- 不得把请求 `params` 当成 RSA 去解
- 不得在 `version=1.1` 下把响应 `data` 按 XXTea 解密

## 4. 当前实现算法

### 4.1 目录树递归

当前实现固定从根开始：

1. 调用 `getReginWithGroupList(regionId="")` 获取首层目录。
2. 对返回的每个目录节点落地最小目录信息：
   - `id`
   - `name`
   - `regionCode`
   - `level`
   - `hasChildren`
   - `havDevice`
   - `regionGBId`
3. 若 `hasChildren=1`，继续传当前目录 `id` 递归拉取下级目录。
4. 即使 `havDevice=0`，也不会阻止对子目录继续递归。
5. 空目录会被保留，不会因为无设备而被吞掉。

### 4.2 目录层级设备分页

当前实现对每个 `havDevice=1` 的目录执行：

1. 调用 `getDeviceList(regionId=<当前目录id>, pageNo=1, pageSize=50)`。
2. 继续按页拉取当前目录层级设备。
3. 设备最小落地字段仅依赖文档保证的：
   - `deviceCode`
   - `deviceName`
4. 不会因为设备无坐标、离线、无云存、无法预览而从“全量目录设备”口径中丢掉。
5. 如遇双目设备或子通道编码，按平台返回口径保留，不擅自吞掉通道设备。

### 4.3 分页收口策略

当前实现遵循以下最小收口规则：

- 正常情况下，按 `totalCount` 与分页结果拉全目录设备。
- 如果平台 `totalCount` 低于当前页实际返回条数，当前实现不会把已返回设备误判为失败。
- 遇到“短页”或空页时会结束当前目录分页。
- 只有在已拉回设备数仍小于平台 `totalCount` 时，才认定该目录分页未拉全并中止整轮快照替换。

该收口策略来自本轮真实平台样例，目的是避免把已返回目录设备误判为失败，同时仍保留“少于平台总数即失败”的一致性约束。

## 5. SQLite 落地口径

当前 SQLite 落地包含：

- 目录表：保存目录 id、名称、父子关系、层级、`regionCode`、`hasChildren`、`hasDevice`、`regionGBId` 等字段。
- 设备表：保存目录层级设备的 `deviceCode`、`deviceName` 及所属目录。
- 同步元数据表：保存平台目录数、平台设备数、对账是否完成、对账是否一致、已对账 regionCode 数、对账设备数、对账在线数和对账范围说明。

当前落地策略为：

- 全量同步成功后才整体替换 SQLite 快照。
- 任一目录树拉取或目录设备分页拉取失败时，不替换快照。
- 因此本地不会出现“半新半旧”的误导性目录快照。

## 6. 当前 UI 展示口径

当前 UI 至少已经对齐到以下口径：

- 明确展示真实监控目录树与所有目录层级设备。
- 人工可以区分哪些是目录节点，哪些是设备。
- 能看到目录名称、父子层级关系、目录设备数。
- 能看到设备名称和设备编码。
- 能看到目录总数、设备总数、SQLite 快照目录数 / 设备数。
- 能看到首层 `getCusDeviceCount` 的最小一致性提示。
- 空目录保持可见。
- 不做复杂搜索、不做分页器、不做排序器、不做批量操作。

## 7. 最小对账范围

当前已接入的最小对账范围为：

- 使用 `getCusDeviceCount(regionCode="")`
- 对账对象为首层 `regionCode`
- 对账维度为：
  - 平台目录树节点数
  - 平台设备拉回数
  - SQLite 落地目录数 / 设备数
  - UI 绑定目录数 / 设备数
  - 首层 `regionCode` 的设备总数 / 在线数

当前**尚未**完成的范围：

- 整棵目录树逐节点 `regionCode` 对账
- 所有子目录层级的逐节点平台计数核验

因此，当前不得把“首层最小对账”写成“整棵树已全部对账完成”。

## 8. 真实结果样例

2026-03-29 在本机执行：

```text
dotnet run --project src/Tysl.Inspection.Desktop.App -- --sync-once
```

得到的真实结果样例为：

```text
SYNC SUMMARY directories=10 devices=19 success=10 failure=0 snapshotReplaced=True platformDirectories=10 platformDevices=19 reconcileCompleted=True reconcileMatched=False reconcileScope=首层 regionCode：94943605-3, 94943605-5, 94943605-2, 94943605-6, 94943605-1；差异：交通要道(94943605-4) 缺少平台对账记录 | 我的设备(94943605-6) 本地7/平台5
```

该样例说明：

- 监控目录树递归、目录设备分页和 SQLite 快照替换已成功跑通。
- 当前已完成首层 `regionCode` 最小对账。
- 当前仍存在首层对账差异，不能写成“已确认完整”。

## 9. 当前日志要求

本轮链路至少记录以下日志：

- 监控目录树递归开始 / 完成
- 单个目录设备分页拉取结果
- 全量同步汇总
- SQLite 快照替换结果
- UI 目录 / 设备绑定结果
- `getCusDeviceCount` 最小对账结果

日志不新增大段调试噪音；若涉及 RTSP，仍只允许掩码显示。

## 10. 当前风险与后续最小任务

当前已知风险：

- 平台首层 `getCusDeviceCount` 返回中缺少 `交通要道(94943605-4)` 对账记录。
- `我的设备(94943605-6)` 存在“本地 7 / 平台 5”的首层口径差异。
- `getDeviceList` 文档最小字段不足以直接覆盖经纬度、在线状态、云存状态等丰富字段。

当前推荐的下一步最小任务为：

**首层 regionCode 对账差异收口**

只建议继续做：

1. 核对缺失对账记录是平台返回缺项还是本地目录 / `regionCode` 映射问题。
2. 核对“本地 7 / 平台 5”的首层差异是否由平台统计口径、目录归属或子通道口径引起。
3. 保持当前主链路不扩展到工单、通知、复杂筛选、复杂地图重构或点位详情大页。
