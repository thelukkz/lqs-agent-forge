# Model Context Protocol (MCP)

## Czym jest MCP?

Model Context Protocol (MCP) to otwarty protokół, który standaryzuje sposób, w jaki aplikacje dostarczają kontekst do dużych modeli językowych (LLM). Działa jak most między modelami AI a zewnętrznymi źródłami danych i narzędziami.

## Problem, który rozwiązuje

Tradycyjne integracje AI wymagają napisania osobnego kodu dla każdej kombinacji modelu i źródła danych. MCP wprowadza wspólny standard, dzięki któremu:

- serwery MCP mogą udostępniać narzędzia, zasoby i prompty
- klienci MCP (aplikacje AI) mogą korzystać z dowolnego serwera MCP
- nie trzeba pisać nowego kodu dla każdej integracji

## Główne komponenty

### Serwer MCP

Serwer MCP udostępnia trzy typy możliwości:

1. **Narzędzia** — funkcje, które model może wywołać, np. wyszukiwanie w bazie danych, operacje na plikach, wywołania API.
2. **Zasoby** — dane tylko do odczytu, do których model może sięgnąć, np. zawartość pliku, wyniki zapytania SQL.
3. **Prompty** — szablony wiadomości, które aplikacja może pobrać i użyć jako punkt wyjścia konwersacji.

### Klient MCP

Klient MCP to aplikacja (zwykle oparta na LLM), która łączy się z serwerami MCP i korzysta z ich możliwości. Klient może:

- wylistować dostępne narzędzia, zasoby i prompty
- wywoływać narzędzia i odczytywać zasoby
- używać szablonów promptów

### Transporty

MCP wspiera kilka mechanizmów transportu:

| Transport | Opis | Zastosowanie |
|-----------|------|--------------|
| stdio | Komunikacja przez stdin/stdout | Procesy lokalne |
| HTTP + SSE | HTTP z Server-Sent Events | Serwery zdalne |
| Streamable HTTP | Nowy standard HTTP | Serwery zdalne (v1+) |

## Przykład użycia

Poniżej prosty scenariusz: agent AI tłumaczący pliki przy pomocy serwera MCP do operacji na plikach.

```
Klient (Agent AI)
    │
    ├── łączy się z serwerem MCP (stdio)
    ├── pobiera listę narzędzi: fs_read, fs_write, fs_manage
    │
    └── pętla agenta:
        ├── wysyła prompt do LLM wraz z definicjami narzędzi
        ├── LLM zwraca wywołanie narzędzia (np. fs_read)
        ├── klient wykonuje narzędzie przez MCP
        ├── wynik trafia z powrotem do LLM
        └── powtarza aż LLM zwróci odpowiedź końcową
```

## Dlaczego MCP?

Przed MCP każda aplikacja AI musiała implementować własną integrację z każdym narzędziem. MCP rozwiązuje ten problem przez standaryzację — serwer napisany raz działa z każdym klientem MCP, niezależnie od modelu czy platformy.

To jak USB dla integracji AI: jeden standard, nieograniczona liczba urządzeń.
