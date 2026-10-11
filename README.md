# WeatherReport

Консольное приложение на C# (.NET 10). Загружает список городов, получает текущую погоду через [wttr.in](https://wttr.in), выводит температуру по каждому городу и статистику по странам.

Внешние пакеты не используются, только `System.Text.Json` и стандартная библиотека .NET.

## Что делает приложение

1. Загружает список городов по URL и убирает дубликаты (без учёта регистра).
2. Для каждого города запрашивает `https://wttr.in/{city}?format=j1` (запросы выполняются параллельно).
3. Из ответа берёт текущую температуру (`current_condition[0].temp_C`) и страну (`nearest_area[0].country[0].value`).
4. Выводит данные по каждому городу.
5. Группирует города по странам и выводит количество городов, среднюю, минимальную и максимальную температуру.

Пример вывода:

```
Vienna, Austria +13 °C
Izhevsk, Russia +4 °C
Moscow, Russia +9 °C

Austria — 1 cities, avg: +13 °C, min: +13 °C, max: +13 °C
Russia — 2 cities, avg: +7 °C, min: +4 °C, max: +9 °C
```

Если данные по городу получить не удалось, он пропускается, а причина выводится в stderr. Если температура неизвестна, выводится `unspecified`.

## Требования

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Доступ в интернет к адресам списка городов и wttr.in

## Структура проекта

```
WeatherReport/
├── .gitignore
├── README.md
└── src/WeatherReport/
    ├── WeatherReport.csproj
    ├── Program.cs                      # точка входа, вывод результата
    ├── Constants.cs                    # ключи настроек и общие константы
    ├── Types.cs                        # WeatherData, CountryStatistics
    ├── appsettings.json                # настройки для Debug
    ├── .env.example                    # шаблон настроек для Release
    ├── Configuration/
    │   └── AppConfiguration.cs         # чтение настроек
    └── Services/
        ├── CityListClient.cs           # загрузка списка городов
        └── WeatherClient.cs            # запрос погоды
```

## Настройка

Адреса и таймаут не хранятся в коде. Источник настроек зависит от конфигурации сборки.

### Debug: `appsettings.json`

Файл лежит в `src/WeatherReport/` и копируется в папку сборки:

```json
{
  "CityListUrl": "https://gistpad.com/raw/vk-task-14",
  "WeatherUrlTemplate": "https://wttr.in/{0}?format=j1",
  "HttpClientTimeoutS": "30"
}
```

`{0}` в шаблоне заменяется на название города.

### Release: переменные окружения или `.env`

| Переменная              | Описание                                      |
|-------------------------|-----------------------------------------------|
| `CITY_LIST_URL`         | Адрес файла со списком городов                |
| `WEATHER_URL_TEMPLATE`  | Шаблон адреса API погоды, `{0}` — город       |
| `HTTP_CLIENT_TIMEOUT_S` | Таймаут HTTP-запросов в секундах              |

Если рядом с исполняемым файлом лежит `.env`, значения читаются из него. Настоящие переменные окружения приоритетнее значений из файла. Файл `.env` в репозиторий не попадает (см. `.gitignore`).

1. Скопируйте шаблон и заполните значения:

   ```bash
   cp src/WeatherReport/.env.example src/WeatherReport/.env
   ```

   Содержимое `.env`:

   ```
   CITY_LIST_URL=https://gistpad.com/raw/vk-task-14
   WEATHER_URL_TEMPLATE=https://wttr.in/{0}?format=j1
   HTTP_CLIENT_TIMEOUT_S=30
   ```

2. Копирование в папку сборки уже настроено в `WeatherReport.csproj`: `appsettings.json` копируется только в Debug, `.env` только в Release.

   ```xml
   <Project Sdk="Microsoft.NET.Sdk">

     <PropertyGroup>
       <OutputType>Exe</OutputType>
       <TargetFramework>net10.0</TargetFramework>
       <ImplicitUsings>enable</ImplicitUsings>
       <Nullable>enable</Nullable>
     </PropertyGroup>

     <ItemGroup Condition="'$(Configuration)' == 'Debug'">
       <None Update="appsettings.json" CopyToOutputDirectory="PreserveNewest" />
     </ItemGroup>
     <ItemGroup Condition="'$(Configuration)' == 'Release'">
       <None Update=".env" CopyToOutputDirectory="PreserveNewest" />
     </ItemGroup>

   </Project>
   ```

   Файл `.env` должен лежать в папке проекта рядом с `.csproj`, иначе при сборке он не будет найден.

## Запуск

```bash
# Debug (настройки из appsettings.json)
dotnet run --project src

# Release (настройки из .env или переменных окружения)
dotnet run --project src -c Release
```

Запуск без `.env` с переменными окружения:

```bash
export CITY_LIST_URL="https://gistpad.com/raw/vk-task-14"
export WEATHER_URL_TEMPLATE="https://wttr.in/{0}?format=j1"
export HTTP_CLIENT_TIMEOUT_S="30"
dotnet run --project src -c Release
```

## Формат списка городов

Один город на строку, например:

```
Moscow
Khabarovsk
Vienna
```

Пустые строки и дубликаты игнорируются.

## Обработка ошибок

- Ошибка по отдельному городу (таймаут, неожиданный ответ API): город пропускается, сообщение выводится в stderr, остальные обрабатываются.
- Ошибка загрузки списка городов или отсутствие обязательной настройки: сообщение в stderr, приложение завершается.
