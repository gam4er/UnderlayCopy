# UnderlayCopy

`UnderlayCopy` — консольный инструмент на .NET (C#) для низкоуровневого чтения NTFS и извлечения защищённых/заблокированных файлов напрямую с тома.

Он работает без VSS и без обычного «высокоуровневого» копирования через `File.Copy`: вместо этого программа получает метаданные размещения файла и читает соответствующие сектора с `\\.\C:` (или другого тома).

## Что делает программа

Поддерживаются два режима:

- **MFT** — читает запись файла в `$MFT`, разбирает атрибут `$DATA` и его data runs, после чего восстанавливает файл по кластерам.
- **Metadata** — получает extents (карта VCN→LCN), затем копирует данные с диска по этим extents.
  - backend 1: `fsutil file queryextents`
  - backend 2: WinAPI `FSCTL_GET_RETRIEVAL_POINTERS` (через `DeviceIoControl`)

## Требования

- Windows
- NTFS-том
- Права **Administrator**
- .NET 8 SDK/runtime

## Сборка

```powershell
dotnet build
```

## Использование

```powershell
# Чтение через MFT
dotnet run -- --mode MFT --source C:\Windows\System32\config\SAM --destination C:\Temp\SAM.dmp

# Чтение через extents из fsutil
dotnet run -- --mode Metadata --source C:\Windows\NTDS\ntds.dit --destination C:\Temp\ntds.dmp

# Чтение через extents из WinAPI (FSCTL_GET_RETRIEVAL_POINTERS)
dotnet run -- --mode Metadata --source C:\Windows\NTDS\ntds.dit --destination C:\Temp\ntds.dmp --metadata-winapi true
```

Дополнительно:

```powershell
# Другой том
dotnet run -- --mode MFT --source <sourcePath> --destination <destPath> --volume \\.\D:

# Флаг можно передать без значения (будет true)
dotnet run -- --mode Metadata --source <sourcePath> --destination <destPath> --metadata-winapi
```

## Как это работает внутри (по шагам)

1. Программа проверяет, что запущена на Windows и от администратора.
2. Открывает сырой том (`\\.\C:`) как `FileStream` для чтения.
3. Читает NTFS Boot Sector и получает размер кластера + расположение `$MFT`.
4. В зависимости от режима:
   - **MFT**:
     - получает FRN/MFT Record Number через `GetFileInformationByHandle`;
     - читает нужную MFT-запись;
     - разбирает атрибуты и `data runs`;
     - читает кластеры напрямую и собирает итоговый файл.
   - **Metadata**:
     - получает extents (`VCN/LCN/length`) через `fsutil` или `FSCTL_GET_RETRIEVAL_POINTERS`;
     - последовательно копирует соответствующие диапазоны кластеров с тома;
     - обрезает вывод до реального размера исходного файла.

## Документация Microsoft (ключевые API)

### 1) Доступ к диску / устройству

- Именование устройств вида `\\.\C:` и `\\?\`:
  - Win32 file namespace: https://learn.microsoft.com/windows/win32/fileio/naming-a-file
- `CreateFile` для открытия файла/тома/устройства:
  - https://learn.microsoft.com/windows/win32/api/fileapi/nf-fileapi-createfilew

### 2) Получение информации о файле и его физическом расположении

- Получение идентификатора файла (FRN/FileIndex) и базовых метаданных:
  - `GetFileInformationByHandle`: https://learn.microsoft.com/windows/win32/api/fileapi/nf-fileapi-getfileinformationbyhandle
- Получение extents (где физически лежат кластеры файла):
  - `FSCTL_GET_RETRIEVAL_POINTERS`: https://learn.microsoft.com/windows/win32/api/winioctl/ni-winioctl-fsctl_get_retrieval_pointers
- Вызов управляющих кодов файловой системы:
  - `DeviceIoControl`: https://learn.microsoft.com/windows/win32/api/ioapiset/nf-ioapiset-deviceiocontrol
- Служебный CLI для extents:
  - `fsutil file queryextents`: https://learn.microsoft.com/windows-server/administration/windows-commands/fsutil-file

## Обобщение метода чтения «заблокированных» файлов

Идея такого подхода в том, что блокировка файла обычно мешает **высокоуровневому** открытию файла по пути, но не всегда мешает:

1. открыть **том** на низком уровне;
2. узнать физическое расположение данных файла (через MFT/extents);
3. прочитать нужные кластеры напрямую.

Это не «магический обход» любой защиты:

- нужны высокие привилегии;
- критичны корректные метаданные NTFS;
- могут быть особенности у sparse/compressed/encrypted файлов;
- защитные средства (EDR/Defender) часто мониторят такой паттерн поведения.

Для DFIR/форензики этот подход полезен тем, что даёт контролируемое и воспроизводимое низкоуровневое извлечение артефактов.

## Для защитников (Defender/EDR)

- Мониторить чтение сырых устройств (`\\.\PhysicalDrive*`, `\\.\C:`).
- Отслеживать вызовы/паттерны `FSCTL_GET_RETRIEVAL_POINTERS` и аномальное использование `fsutil file queryextents`.
- Контролировать доступ к чувствительным файлам (`SAM`, `SYSTEM`, `SECURITY`, `NTDS.dit`) даже при попытках чтения «в обход» стандартных API.

## Этическое использование

Инструмент предназначен для исследований, тестирования защиты и DFIR. Используйте только на системах, где у вас есть явное разрешение.
