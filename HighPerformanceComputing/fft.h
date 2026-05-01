#pragma once

// 定义宏：当在DLL内部编译时使用 dllexport，在外部使用时使用 dllimport
#ifdef HIGHPERFORMANCECOMPUTING_EXPORTS
#define HIGHPERFORMANCECOMPUTING_API __declspec(dllexport)
#else
#define HIGHPERFORMANCECOMPUTING_API __declspec(dllimport)
#endif

// extern "C" 用于防止 C++ 的名称重整(Name Mangling)，确保函数名在编译后不被改变
extern "C" {
    // 声明一个简单的加法函数
    HIGHPERFORMANCECOMPUTING_API int Add(int a, int b, int c);
}