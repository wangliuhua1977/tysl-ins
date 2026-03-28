# 天翼视联开放平台接入规则（精简版）

## 1. 通用通信规则
- 协议：HTTPS
- 请求方式：POST
- Content-Type：application/x-www-form-urlencoded
- 请求头必须带：`apiVersion=2.0`
- 字符编码：UTF-8

## 2. 公共请求参数
业务接口请求时，私有参数需先加密为 `params`，再与以下公共参数一起提交：

- `appId`
- `clientType`
- `params`
- `timestamp`
- `version`
- `signature`

## 3. clientType 约定
当前桌面项目固定使用：
- `clientType = 3`（PC）

## 4. version 约定
当前项目固定使用：
- `version = 1.1`

说明：
- 请求参数加密仍使用 XXTea
- `version = 1.1` 时，响应 `data` 按 RSA 私钥解密

## 5. params 组装与加密
### 私有业务参数
将接口私有参数按如下形式拼接为原始字符串：

`参数1=值&参数2=值&...`

注意：
- 文档说明请求参数无须排序
- 使用 `AppSecret` 对该字符串进行 XXTea 加密
- 加密结果转为十六进制字符串后作为 `params`

## 6. signature 计算规则
签名算法：

`HMAC-SHA256(appId + clientType + params + timestamp + version, appSecret)`

注意：
- 字段顺序必须严格为：`appId + clientType + params + timestamp + version`
- 不要自行改顺序

## 7. RSA / XXTea 规则
### 请求侧
- 私有参数：XXTea 加密
- 密钥：`AppSecret`

### 响应侧
- 当 `version=1.1`
- 响应字段 `data` 使用 RSA 私钥解密

## 8. 获取 accessToken
### 接口路径
`/open/oauth/getAccessToken`

### 完整地址
`https://vcp.21cn.com/open/oauth/getAccessToken`

### grantType
用户无感知获取令牌：
- `grantType = vcp_189`

刷新令牌：
- `grantType = refresh_token`

## 9. 令牌有效期
用户无感知方式：
- `accessToken`：7 天
- `refreshToken`：30 天

## 10. 令牌使用原则
- 不要每次调用都重新获取 token
- 应本地缓存 token
- 过期后刷新
- 不要把 accessToken / refreshToken 固化到公开仓库配置文件中

## 11. 当前项目配置建议
公开仓库中的 `appsettings.json` 仅保留占位符。
真实值只放在本机 `appsettings.Local.json`：
- AppId
- AppSecret
- RSA 私钥
- EnterpriseUser
- 地图密钥
- Webhook

## 12. 联通性验证建议
当前项目只做最小联通性验证：
1. 调用 `getAccessToken`
2. 成功则说明签名、加密、请求头、网络链路基本可用
3. 若平台返回白名单拦截，则优先处理出口公网 IP 白名单

## 13. 敏感信息与日志规则
- 日志中不得完整明文输出 AppSecret
- 不得完整明文输出 RSA 私钥
- 不得完整明文输出 token
- 日志中仅允许掩码显示敏感字段

## 14. 当前工程已验证的实现细节
- `timestamp` 需要使用 Unix 毫秒时间戳
- `signature` 可按 `HMAC-SHA256(..., appSecret)` 的十六进制结果提交
- `params` 需要使用 `XXTea(私有参数串, AppSecret)` 后转十六进制字符串提交
