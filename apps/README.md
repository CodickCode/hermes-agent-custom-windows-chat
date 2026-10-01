# Наши виндовые приложения

Два отдельных приложения под Windows. Каждое живёт в своей папке и собирается независимо.

| Папка | Что это | Стек |
|---|---|---|
| `hermes-chat-windows/` | HermesChat — виндовс-чат под Hermes агента | WPF .NET 8, self-contained single-file |
| `pulsepilot/` | PulsePilot — диспетчер параллельных проектов (дерево задач, фокус-циклы) | WPF .NET 8, self-contained single-file |

## Про что каждое

**HermesChat.** Тонкий клиент к `api_server` Hermes (`hermes gateway`). Отправляет запрос
в `POST /v1/runs`, читает ответ из SSE-потока `/v1/runs/{id}/events`, умеет остановить
запуск. Агента внутри приложения нет — вся работа выполняется на стороне шлюза, поэтому
приложение получает доступ к инструментам Hermes (файлы, терминал, веб).

**PulsePilot.** Держит дерево проектов и TODO, следит за фокусом, готовит поручения
и передаёт их исполнителям через Agent Bridge. Это приложение не зависит от HermesChat
и не требует шлюза для базовой работы.

## Сборка любого из них

Нужен .NET SDK 8. Из корня папки приложения:

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist
```

Внешних NuGet-пакетов нет ни у одного — только стандартная библиотека и WPF.

## Проверка

У PulsePilot есть консольный тест логики:

```
cd pulsepilot/tests && dotnet run
```
