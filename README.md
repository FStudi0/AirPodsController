# 🎧 AirPods Controller

Управление AirPods Pro на Windows с поддержкой переключения режимов ANC / Прозрачность.

![Version](https://img.shields.io/badge/version-2.1-blue)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-blue)
![.NET](https://img.shields.io/badge/.NET-8-purple)

## ✨ Возможности

- ✅ Переключение ANC / Прозрачность из приложения
- ✅ Горячие клавиши для быстрого переключения режимов
- ✅ Авто-подключение AirPods при запуске Windows
- ✅ Трей-иконка для быстрого доступа
- ✅ Сохранение настроек между сессиями
- ✅ Material Design UI

## 📋 Требования

| Компонент | Описание |
|-----------|----------|
| OS | Windows 10 / 11 (x64) |
| Runtime | [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |
| Режим | **Тестовый режим Windows** (обязательно для драйвера) |
| BIOS | **Secure Boot отключён** |

## 🚀 Установка

### 1. Подготовка системы

&gt; ⚠️ **ВАЖНО:** Для работы программы необходим тестовый режим Windows!

**Шаг 1:** Отключите Secure Boot в BIOS/UEFI

**Шаг 2:** Включите тестовый режим Windows (CMD от Администратора):
```cmd
bcdedit /set testsigning on

**Шаг 3:** Перезагрузите компьютер
