#!/bin/bash

echo "🦈 Rinha de Backend 2025 - C# .NET 9 AOT Version"
echo "=================================================="
echo ""
echo "⚠️  ATENÇÃO: Execute primeiro o payment-processor separadamente:"
echo "   cd rinha-de-backend-2025/payment-processor"
echo "   docker-compose up -d"
echo ""

# Verificar Docker
if ! command -v docker &> /dev/null; then
    echo "❌ Docker não encontrado. Instale o Docker primeiro."
    exit 1
fi

# Detectar versão do Docker Compose
if docker compose version &> /dev/null; then
    DOCKER_COMPOSE="docker compose"
elif command -v docker-compose &> /dev/null; then
    DOCKER_COMPOSE="docker-compose"
else
    echo "❌ Docker Compose não encontrado. Instale o Docker Compose primeiro."
    exit 1
fi

echo "🐳 Usando: $DOCKER_COMPOSE"

# Verificar se a rede do payment-processor existe
if ! docker network ls | grep -q "payment-processor"; then
    echo "❌ Rede 'payment-processor' não encontrada!"
    echo "   Execute primeiro: cd rinha-de-backend-2025/payment-processor && docker-compose up -d"
    exit 1
fi

echo "✅ Rede 'payment-processor' encontrada"

# Limpar containers existentes
echo "🧹 Limpando containers existentes..."
$DOCKER_COMPOSE down --remove-orphans 2>/dev/null || true

# Limpar sockets existentes
echo "🧹 Limpando sockets existentes..."
sudo rm -f /tmp/gateway-*.sock 2>/dev/null || true

# Build e start
echo "🔨 Fazendo build e iniciando containers..."
$DOCKER_COMPOSE up -d --build

# Aguardar inicialização
echo "⏳ Aguardando inicialização dos serviços..."
sleep 10

# Verificar se os serviços estão rodando
echo "🔍 Verificando status dos serviços..."
$DOCKER_COMPOSE ps

# Teste simples
echo "🧪 Testando endpoint..."
curl -s -o /dev/null -w "Status: %{http_code}\n" http://localhost:9999/payments-summary || echo "❌ Falha no teste"

# Sempre investigar para debug
echo ""
echo "🔍 Diagnóstico completo..."
echo "📋 Logs do gateway 1:"
docker logs rinha-gateway-1 2>&1 | tail -10
echo ""
echo "📋 Logs do gateway 2:"
docker logs rinha-gateway-2 2>&1 | tail -10
echo ""
echo "📋 Logs do database:"
docker logs rinha-db 2>&1 | tail -10
echo ""
echo "📋 Testando conectividade entre containers:"
echo "   Gateway → Database:"
docker exec rinha-gateway-1 curl -s http://rinha-db:8080/summary 2>/dev/null | head -c 100 || echo "   ❌ Falha na conexão gateway→database"
echo ""
echo "   Gateway → Payment Processor Default:"
docker exec rinha-gateway-1 curl -s http://payment-processor-default:8080/ 2>/dev/null | head -c 100 || echo "   ❌ Falha na conexão gateway→processor-default"
echo ""
echo "   Gateway → Payment Processor Fallback:"
docker exec rinha-gateway-1 curl -s http://payment-processor-fallback:8080/ 2>/dev/null | head -c 100 || echo "   ❌ Falha na conexão gateway→processor-fallback"

# Se falhou, vamos investigar mais
if ! curl -s -f http://localhost:9999/payments-summary > /dev/null; then
    echo ""
    echo "🔍 Investigação adicional para 502..."
    echo "📋 Logs do nginx:"
    docker logs rinha-nginx 2>&1 | tail -10
    echo ""
    echo "📋 Sockets no nginx container:"
    docker exec rinha-nginx ls -la /tmp/gateway-*.sock 2>/dev/null || echo "   Nenhum socket encontrado no nginx"
    echo ""
    echo "📋 Testando conexão direta aos sockets:"
    docker exec rinha-nginx curl -s --unix-socket /tmp/gateway-1.sock http://localhost/payments-summary 2>/dev/null || echo "   Falha na conexão com socket 1"
    echo ""
    echo "📋 Status dos gateways:"
    docker logs rinha-gateway-1 2>&1 | tail -5
    echo ""
    echo "📋 Error logs do nginx:"
    docker exec rinha-nginx cat /var/log/nginx/error.log 2>/dev/null | tail -5 || echo "   Nenhum error log encontrado"
fi

echo ""
echo "✅ Setup concluído!"
echo "🌐 API disponível em: http://localhost:9999"
echo "📊 Endpoint de teste: http://localhost:9999/payments-summary"
echo ""
echo "📡 Payment Processors (externos):"
echo "   Default: http://localhost:8001"
echo "   Fallback: http://localhost:8002"
echo ""
echo "Para parar o backend: $DOCKER_COMPOSE down"
echo "Para parar os payment-processors: cd rinha-de-backend-2025/payment-processor && docker-compose down"
