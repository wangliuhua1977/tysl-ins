# 分组与设备同步接口说明（当前轮次专用）

## 1. 本轮目标
当前轮次只实现“点位同步最小链路”：
- 获取分组列表
- 获取分组下设备列表
- 写入本地 SQLite
- 在 UI 展示最小同步结果与总览统计

不实现：
- 区域树
- 设备详情
- 巡检
- 工单
- 通知
- 地图真实渲染

---

## 2. 接口一：获取分组列表
### 接口名称
`getGroupList`

### 完整地址
`https://vcp.21cn.com/open/token/vcpGroup/getGroupList`

### 请求方式
POST

### 请求参数
- `accessToken`：必填
- `enterpriseUser`：用户无感知获取令牌时需要传

### 响应主要字段
返回 `data` 列表，每项包含：
- `groupId`
- `groupName`
- `deviceCount`

### 结果用途
写入本地 `Group` 表。

---

## 3. 接口二：获取分组下设备列表
### 接口名称
`getGroupDeviceList`

### 完整地址
`https://vcp.21cn.com/open/token/vcpGroup/getGroupDeviceList`

### 请求方式
POST

### 请求参数
- `accessToken`：必填
- `enterpriseUser`：用户无感知获取令牌时需要传
- `groupId`：必填

### 响应主要字段
返回 `data` 列表，每项可关注以下字段：
- `deviceCode`
- `deviceName`
- `latitude`
- `longitude`
- `location`
- `onlineStatus`
- `cloudStatus`
- `bandStatus`
- `sourceTypeFlag`

### 结果用途
写入本地 `Device` 表。

---

## 4. 可选接口（本轮非必须）
### getGroupInfo
按设备编码查询分组信息。

本轮不是必需接口，先不做。

---

## 5. 本地数据库最小表结构建议
## Group 表
建议字段：
- `groupId` TEXT / INTEGER PRIMARY KEY
- `groupName` TEXT NOT NULL
- `deviceCount` INTEGER
- `syncedAt` TEXT NOT NULL

## Device 表
建议字段：
- `deviceCode` TEXT PRIMARY KEY
- `deviceName` TEXT
- `groupId` TEXT NOT NULL
- `latitude` TEXT NULL
- `longitude` TEXT NULL
- `location` TEXT NULL
- `onlineStatus` INTEGER / BOOLEAN NULL
- `cloudStatus` INTEGER / BOOLEAN NULL
- `bandStatus` INTEGER / BOOLEAN NULL
- `sourceTypeFlag` INTEGER NULL
- `syncedAt` TEXT NOT NULL

建议为 `groupId` 建索引。

---

## 6. 同步流程建议
1. 先获取 `accessToken`（复用本地缓存）
2. 调用 `getGroupList`
3. 写入 / 更新本地 Group 表
4. 遍历每个分组调用 `getGroupDeviceList`
5. 写入 / 更新本地 Device 表
6. 统计：
   - 分组数
   - 设备数
   - 成功数
   - 失败数
   - 最近同步时间

---

## 7. 运行总览页当前轮次最小统计
在总览页只接这 4 个统计：
- 点位总数
- 在线数
- 离线数
- 未定位数（纬度或经度为空）

说明：
- 当前统计来自本地 SQLite
- 不直接实时查平台

---

## 8. 失败分类建议
本轮至少区分：
- `getGroupList` 失败
- `getGroupDeviceList` 失败
- 数据库存储失败

API 访问日志需继续保留：
- 调用时间
- 接口名称
- 请求地址
- 请求参数（脱敏）
- 请求头（脱敏）
- 响应结果（脱敏）
- 响应码
- 耗时
- 异常信息

说明：
- 当前工程已验证 `getAccessToken` 和同步接口继续沿用同一网关实现
- `params` 密文提交格式需与认证文档保持一致：XXTea 后转十六进制字符串

---

## 9. 当前轮次 UI 要求
只补一个入口：
- 基础配置页中的“同步分组与设备”按钮

同步完成后显示：
- 分组数
- 设备数
- 成功 / 失败数
- 最近同步时间

不做：
- 复杂表格
- 编辑
- 设备详情弹层
- 地图点位渲染

---

## 10. 当前轮次禁止项
- 不实现区域树
- 不实现设备详情
- 不实现巡检
- 不实现地图真实点位渲染
- 不实现工单和通知
- 不要超出本轮范围
