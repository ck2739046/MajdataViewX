@echo off
setlocal
chcp 65001 >nul
title Clean Unity Bee cache
cd /d "%~dp0"

echo ============================================
echo  正在终止残留的 Unity/Bee 构建进程...
echo ============================================

rem 结束残留的 Unity Bee 后端（它可能锁住状态文件导致 rename 失败）
taskkill /F /IM bee_backend.exe >nul 2>&1
rem 结束失败构建遗留、挂起并锁住 .obj 的 MSVC 链接器
taskkill /F /IM link.exe >nul 2>&1
rem 结束残留的 IL2CPP 运行器（若被锁需手动关闭 Unity 后重试）
taskkill /F /IM Unity.ILPP.Runner.exe >nul 2>&1

timeout /t 2 /nobreak >nul

echo.
echo ============================================
echo  正在删除 Library\Bee 缓存（Unity 会自动重建）...
echo ============================================

if exist "Library\Bee" (
    rd /s /q "Library\Bee"
    if exist "Library\Bee" (
        echo [警告] Library\Bee 中仍有文件被占用。
        echo        请先完全关闭 Unity 及所有构建进程，再重新运行本脚本。
    ) else (
        echo Library\Bee 已删除。
    )
) else (
    echo 未发现 Library\Bee，无需清理。
)

echo.
echo 完成。重新打开 Unity 并重新触发构建即可。
endlocal
