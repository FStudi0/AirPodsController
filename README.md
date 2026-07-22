# <img src="https://github.com/user-attachments/assets/a8f9f3c9-345b-4961-ae6a-9420a9d36965" width="5%"> AirPods Controller

Управление AirPods Pro на Windows с поддержкой переключения режимов ANC / Прозрачность.

![Version](https://img.shields.io/badge/version-2.1-blue)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-blue)
![.NET](https://img.shields.io/badge/.NET-8-purple)

<img src="https://github.com/user-attachments/assets/a9d5915b-c2c1-4471-8bb5-d8cd68336780" width="50%">

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

&gt; ⚠️ **ВАЖНО:** Для работы программы необходим тестовый режим Windows! А так же сделайте резевную точку восстановления (на всякий случай).

**Шаг 1:** Отключите Secure Boot в BIOS/UEFI

**Шаг 2:** Включите тестовый режим Windows (CMD от Администратора):
```cmd
bcdedit /set testsigning on
```
**Шаг 3:** Перезагрузите компьютер

### &gt; Установка программы

**1** Скачайте AirPodsControl.zip из Releases ->
**2** Запустите установщик от имени Администратора ->
**3** Следуйте инструкциям мастера установки

## Установка драйвера
**📦 Драйвер входит в состав архива.
Зайдите в архив AirPodsControl.zip из Releases, найдите папку Driver внутри архива и установите вручную через Диспетчер устройств (см. ниже).**

<img src="https://github.com/user-attachments/assets/6834cc58-4b49-42af-bf8d-e354ff2ed11a" width="20%">

## Порядок установки 

<img src="https://github.com/user-attachments/assets/46642469-7b8d-439b-9180-a5ecb3a7fbd4" width="20%">
<img src="https://github.com/user-attachments/assets/369ea30d-4940-4cce-9534-2d1b98e56747" width="20%">
<img src="https://github.com/user-attachments/assets/e487c01c-c5cd-4d1e-a2db-b6ab1326444c" width="20%">

**Обновить драйвер -> Найти драйверы на этом компьютере -> Выбрать драйвер из списка доступных драйверов на компьютере -> Установить с диска -> Обзор и указывайте путь к папке Driver который находится в архиве. Нажимаете далее и устанавливайте.**

## 🙏 Благодарности

### Драйвер

. [unzoid](https://github.com/unzoid) за разработку драйвера [Airpods-Windows-Control](https://github.com/unzoid/Airpods-Windows-Control)

### Протокол и реверс-инжиниринг
. [AAP-Protocol-Defintion]([https://github.com/unzoid](https://github.com/tyalie/AAP-Protocol-Defintion) - документация протокола Apple Audio Protocol (AAP).
