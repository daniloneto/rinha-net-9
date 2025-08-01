# Rinha de Backend 2025

Este projeto é uma implementação para a competição Rinha de Backend 2025 ( https://github.com/zanfranceschi/rinha-de-backend-2025 ) , focada em performance, escalabilidade e boas práticas de desenvolvimento backend.

## Estrutura do Projeto

- **database/**: Serviço de banco de dados utilizando LockFree.EventStore.
- **gateway/**: Serviço de gateway, responsável por orquestrar as requisições e comunicação entre serviços.
- **nginx.conf**: Configuração do proxy reverso NGINX.
- **docker-compose.yml**: Orquestração dos serviços via Docker Compose.

## Tecnologias e Bibliotecas

### Principais Tecnologias
- **.NET 9** com AOT (Ahead-of-Time) compilation
- **Docker** e **Docker Compose** para containerização
- **NGINX** como proxy reverso
- **Banco de dados customizado** implementado em .NET

### Bibliotecas Utilizadas
- **[UnixDomainSockets.HttpClient](https://www.nuget.org/packages/UnixDomainSockets.HttpClient)**: Biblioteca para comunicação via Unix Domain Sockets entre os serviços, otimizada para performance e compatível com AOT compilation.
- **[LockFree.EventStore v0.1.2](https://www.nuget.org/packages/LockFree.EventStore/)**: Event store em memória lock-free para armazenamento de alta performance com:
  - Particionamento por chave (8 partições configuradas)
  - Agregações funcionais por janela temporal
  - Capacidade configurável (100.000 eventos)
  - Callbacks de observabilidade para eventos descartados
  - Propriedades de estado em tempo real (Count, Capacity, IsEmpty, IsFull)
  - Método Clear() para purge eficiente
  - Zero dependências externas e compatibilidade completa com AOT

### Funcionalidades Específicas
- **Comunicação Inter-Serviços**: Unix Domain Sockets para máxima performance
- **Armazenamento Lock-Free**: MPMC (Multiple Producer, Multiple Consumer) sem locks
- **Observabilidade**: Endpoint `/metrics` para monitoramento em tempo real
- **Descarte FIFO**: Gerenciamento automático de capacidade com descarte de eventos antigos
- **Agregações Temporais**: Consultas eficientes por janela de tempo

## Como Executar

```sh
docker-compose up --build
```

## Serviços

- **Gateway**: Recebe e processa as requisições HTTP, encaminhando para o serviço de banco de dados.
- **Database**: Gerencia o armazenamento e consulta dos dados usando LockFree.EventStore.
- **NGINX**: Proxy reverso para balanceamento de carga e roteamento.

### Endpoints Disponíveis

#### Database Service
- `POST /payments/default` - Adiciona pagamento no store padrão
- `POST /payments/fallback` - Adiciona pagamento no store de fallback
- `GET /summary?from={date}&to={date}` - Obtém resumo de pagamentos por período
- `GET /metrics` - Métricas em tempo real dos event stores (contadores, capacidade, status)
- `POST /purge-payments` - Limpa todos os pagamentos armazenados

#### Gateway Service
- `POST /payments` - Endpoint principal para submissão de pagamentos
- `POST /purge-payments` - Limpa pagamentos (proxy para database service)

## Licença

Este projeto está sob a licença MIT. Veja o arquivo `LICENSE` para mais detalhes.

