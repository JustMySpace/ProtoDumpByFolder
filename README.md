# ProtoFolderDump48

面向 **.NET Framework 4.8** 的 Protobuf 反射导出工具。用于在仅持有已发布的 **.dll/.exe** 时，批量提取并重建 Protobuf 描述信息，生成：

- `out/descriptors.desc`（`FileDescriptorSet`，可被 `protoc` 等直接消费）
- `out/*.recovered.proto`（近似还原的 `.proto`，便于阅读与二次生成）
- `out/resources/**`（若程序集内嵌了 `.proto` 资源，则原样导出）

> 适用于无原始 `.proto` 文件、需要对接或复现通信结构（如内部协议/SDK）的场景。

---

## 功能特性

- **多路径发现**
  - 通过 `*Reflection` 静态入口（如 `FooBarReflection.Descriptor`）
  - 通过消息类型静态 `Descriptor`（`MessageDescriptor.File`）
  - 通过枚举类型静态 `Descriptor`
  - **定向抓取**：传入已知类型名（短名或全名）精确反查，稳定且快速

- **双通道导出**
  - 能获取 `FileDescriptor.Proto/ToProto()`：直接输出原始 `FileDescriptorProto`
  - 否则：基于反射 **重建** 描述（字段/枚举/嵌套类型/service/依赖）

- **资源导出**
  - 自动提取程序集内嵌的 `.proto` 资源文件（若存在）

---

## 运行环境

- Windows，.NET Framework **4.8**
- 需与目标程序集位于兼容架构（x86/x64）环境
- 工具自身依赖 `Google.Protobuf`（已随工程引用）

---

## 构建

1. 使用 Visual Studio 2019/2022 打开解决方案  
2. 目标框架选择 **.NET Framework 4.8**  
3. 还原 NuGet 包并编译（`Debug`/`Release` 均可）

---

## 用法

```bash
ProtoFolderDump48.exe <folderPath> [TypeNamesCsv]
```

- `folderPath`：待扫描根目录（递归查找 `*.dll`、`*.exe`）
- `TypeNamesCsv`（可选）：**定向抓取**的类型名，逗号分隔；支持**短名**或**完全限定名**  
  例如：`ImageDisplayFrameInfo,ImageRgb3Reflection` 或 `VisionSource.CameraManages.ImageDisplayFrameInfo`

### 示例

```bash
# 仅按目录全量扫描（自动兜底）
ProtoFolderDump48.exe "C:\Apps\VisionSource"

# 推荐：提供已知类型名，命中更稳、速度更快
ProtoFolderDump48.exe "C:\Apps\VisionSource" ImageDisplayFrameInfo,ImageRgb3Reflection

# 也可使用完全限定名
ProtoFolderDump48.exe "C:\Apps\VisionSource" VisionSource.CameraManages.ImageDisplayFrameInfo
```

**退出码**
- `0`：程序正常结束（可能未导出任何描述，详见日志）
- `1`：命令行参数错误
- `2`：目录不存在

---

## 输出

所有结果位于当前工作目录的 `out/`：

```
out/
  descriptors.desc                 # FileDescriptorSet
  <file-name>.recovered.proto      # 近似还原的 .proto
  resources/
    <AssemblyName>/
      <EmbeddedResource>.proto     # 程序集内嵌的 .proto（若存在）
```

> 若后续需要生成 C# 等代码，可直接用 `descriptors.desc`：  
> `protoc --descriptor_set_in=out/descriptors.desc --csharp_out=gen .`  
> 或将目标 `*.recovered.proto` 与其依赖一起传给 `protoc`。

---

## 注意事项与限制

- **近似还原**：当目标程序集不暴露 `FileDescriptor.Proto/ToProto()` 时，工具基于反射重建描述：
  - 自定义 `options`、`extensions`、`map` 的细节可能缺失或被简化；
  - `oneof` 会还原名称与归属，但高级选项可能不可得；
  - 字段默认值与注释无法保证完全一致。
- **Well-Known Types (WKT)**：`google/protobuf/*.proto` 不会随目标程序集分发；编译 `*.recovered.proto` 时需在本地 `protoc` 环境中正确引用。
- **依赖完整性**：确保目标目录中包含全部依赖（同架构），否则反射可能受阻。
- **合法使用**：仅用于具备授权的场景（互操作、测试验证、接口对接等）。

---

## 故障排查

- 日志提示 `未找到任何 Protobuf Reflection 定义`  
  - 追加已知类型名重试：  
    `ProtoFolderDump48.exe "<dir>" ImageDisplayFrameInfo,ImageRgb3Reflection`
  - 确认依赖是否齐全、架构是否匹配（x86/x64）
  - 优先使用目标发布目录中的 `Google.Protobuf.dll`

- 大量 `[fd-warn] 无法获取 Proto 字节: xxx.proto`  
  - 属正常现象：目标版本未暴露 `Proto/ToProto()`；工具会自动采用“反射重建”，不影响整体导出

- 编译 `*.recovered.proto` 报缺少 `google/protobuf/*.proto`  
  - 安装/配置 `protoc` 的 WKT（随官方发行版提供）

---

## 许可

请依据你的仓库策略补充许可证（例如 MIT/Apache-2.0）。使用本工具须遵循适用法律与协议。
