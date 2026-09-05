# 基于 v6.2.0 修改

# edit

- 添加 pv offset
- ws 协议拓展 reset
- 新代码高亮主题
- 鼠标悬停在行号上，显示拍号 tooltip

- 与 HachimiDX 通信:
  - 接受 load, reset, exit 指令
  - 发送 play, pause, seek 指令

- 调整界面：
  - 默认启用光标跟随文本
  - 添加"一键回到开头"按钮
  - 添加音符流速快捷设置
  - 主窗口取消毛玻璃，默认 #262626
  - 其他组件样式或布局调整

- 设置修改：
  - 禁用皮肤切换到 trg ui
  - 默认不启用时间轴缓动
  - 背景图片默认不是小小蓝白
  - 禁用全屏背景

- 禁用功能：
  - discord rpc
  - 从 github 检查更新
  - 打开或关闭 viewx
  - 让 viewx 获得焦点

# view

- 添加 pv offset
- ws 协议拓展 reset
- 禁用 全屏按钮 & 分辨率调整下拉菜单
- 调整 legacy ui 样式以匹配正方形
- 添加 note_counter / rate 可见性 按钮开关
- 最小化/闲置时 锁帧
- 导出视频的 pv 解码使用外部 ffmpeg
