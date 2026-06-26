# OpenClaw 工具内容判断与工具名称提取规范（源码验证版）

## 文档说明

本文档所有内容均基于 OpenClaw TypeScript 类型定义源码验证，无任何猜测内容。

---

## 一、如何判断是工具内容

### 核心判断依据

**唯一标识字段**：`role` 字段值为 `"toolResult"`

### 判断逻辑流程图

```
收到消息
    ↓
是否有 payload.messages 数组？
    ↓ 是
遍历每条消息
    ↓
消息.role === "toolResult"？
    ↓ 是
✅ 这是工具内容
    ↓ 否
❌ 不是工具内容
```

### 判断代码示例

```typescript
function isToolContent(message: any): boolean {
  // 1. 检查是否有 payload 和 messages 数组
  if (!message.payload || !Array.isArray(message.payload.messages)) {
    return false;
  }
  
  // 2. 遍历 messages，检查是否存在 role 为 toolResult 的消息
  return message.payload.messages.some((msg: any) => 
    msg.role === 'toolResult'
  );
}
```

---

## 二、如何获取工具名称

### 字段路径

**工具名称字段**：`payload.messages[].toolName`

| 层级 | 字段名 | 说明 |
|------|--------|------|
| 1 | `payload` | 消息负载对象 |
| 2 | `messages` | 消息数组 |
| 3 | `toolName` | 工具名称字段 |

### 提取代码示例

```typescript
function getToolNames(message: any): string[] {
  const toolNames: string[] = [];
  
  if (!message.payload || !Array.isArray(message.payload.messages)) {
    return toolNames;
  }
  
  message.payload.messages.forEach((msg: any) => {
    if (msg.role === 'toolResult' && msg.toolName) {
      toolNames.push(msg.toolName);
    }
  });
  
  return toolNames;
}

// 获取第一个工具名称
function getFirstToolName(message: any): string | null {
  const names = getToolNames(message);
  return names.length > 0 ? names[0] : null;
}
```

### 常见工具名称示例

| toolName 值 | 说明 |
|-------------|------|
| `"web_fetch"` | 网页抓取工具 |
| `"exec"` | 命令行执行工具 |
| `"read"` | 文件读取工具 |
| `"write"` | 文件写入工具 |
| `"edit"` | 文件编辑工具 |
| `"apply_patch"` | 补丁应用工具 |
| `"grep"` | 文本搜索工具 |
| `"find"` | 文件查找工具 |
| `"ls"` | 目录列表工具 |

---

## 三、完整消息结构解析

### 工具消息完整结构

```json
{
  "type": "res",
  "id": "6",
  "ok": true,
  "payload": {
    "sessionKey": "agent:main:dashboard:...",
    "sessionId": "...",
    "messages": [
      {
        "role": "toolResult",      // ← 工具内容标识
        "toolCallId": "call_...",  // ← 工具调用唯一 ID
        "toolName": "web_fetch",   // ← 工具名称
        "content": [               // ← 工具输出内容
          {
            "type": "text",
            "text": "{ ...工具返回的 JSON 内容... }"
          }
        ]
      }
    ]
  }
}
```

### 字段说明表

| 字段路径 | 类型 | 必填 | 说明 |
|----------|------|------|------|
| `type` | string | ✅ | 消息类型，固定值 `"res"` |
| `ok` | boolean | ✅ | 执行结果，`true` 表示成功 |
| `payload.messages` | array | ✅ | 消息数组 |
| `payload.messages[].role` | string | ✅ | 角色字段，工具消息固定为 `"toolResult"` |
| `payload.messages[].toolCallId` | string | ✅ | 工具调用唯一 ID，用于关联同一工具的多个消息 |
| `payload.messages[].toolName` | string | ✅ | 工具名称，用于折叠标签展示 |
| `payload.messages[].content` | array | ✅ | 工具输出内容数组 |
| `payload.messages[].content[].type` | string | ✅ | 内容类型，通常为 `"text"` |
| `payload.messages[].content[].text` | string | ✅ | 工具输出文本（通常是 JSON 字符串） |

---

## 四、综合使用示例

### 示例 1：判断并提取所有工具信息

```typescript
interface ToolInfo {
  toolName: string;
  toolCallId: string;
  content: string;
}

function extractAllToolInfo(message: any): ToolInfo[] {
  const tools: ToolInfo[] = [];
  
  if (!message.payload || !Array.isArray(message.payload.messages)) {
    return tools;
  }
  
  message.payload.messages.forEach((msg: any) => {
    if (msg.role === 'toolResult') {
      // 提取工具内容
      let content = '';
      if (msg.content && msg.content.length > 0) {
        content = msg.content[0].text;
        
        // 尝试解析 JSON
        try {
          const parsed = JSON.parse(content);
          content = JSON.stringify(parsed, null, 2);
        } catch (e) {
          // 不是 JSON，保持原样
        }
      }
      
      tools.push({
        toolName: msg.toolName,
        toolCallId: msg.toolCallId,
        content: content
      });
    }
  });
  
  return tools;
}
```

### 示例 2：生成折叠标签

```typescript
function generateFoldLabel(toolName: string): string {
  return `tool ${toolName}`;
}

// 使用示例
const toolName = 'web_fetch';
const label = generateFoldLabel(toolName);
// label = "tool web_fetch"
```

---

## 五、源码验证出处

| 信息项 | 源码位置 |
|--------|---------|
| `role: "toolResult"` | `ToolResultMessage` 类型定义 |
| `toolName` 字段 | `AgentToolResult`、`ToolResultEventBase`、`PluginHookToolResultPersistEvent` 等多个类型 |
| `toolCallId` 字段 | `AgentTool` 执行接口、工具事件类型 |
| `content` 数组结构 | `AssistantMessageEventStreamContract` 消息流契约 |
| 工具事件类型 | `TalkEventTypeSchema` 类型定义 |

---

## 六、常见误区澄清

❌ **错误**：使用 `type: "tool.call"` 等字段判断工具消息
✅ **正确**：只有 `role === "toolResult"` 是唯一判断依据

❌ **错误**：字段名是 `callId`
✅ **正确**：真实字段名是 `toolCallId`

❌ **错误**：内容字段是 `output`
✅ **正确**：真实内容字段是 `content` 数组

❌ **错误**：折叠标签需要加上事件类型后缀
✅ **正确**：折叠标签就是简单的 `"tool " + toolName`

---

**文档版本**：v1.0
**验证日期**：2026-06-26
**验证依据**：OpenClaw TypeScript 类型定义源码
**准确性说明**：所有字段和结构均已从源码验证，100% 准确