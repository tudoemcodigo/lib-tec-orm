<#
.SYNOPSIS
    Gera de novo o modelo database first (Generated\) a partir do schema "dbfirst" do banco de testes.

.DESCRIPTION
    A string de conexão é lida da variável de ambiente TEC_TESTES_ORM_SQL_CONEXAO (a mesma dos testes de integração) e só existe
    na memória deste processo: nunca é gravada em arquivo nem no código gerado (--no-onconfiguring).
    Em produção, o TEC.ORM obtém a conexão do TEC.Vault; este script é ferramenta de desenvolvimento.

    Defina a variável só na sessão atual, sem deixar no histórico do PowerShell:
        $env:TEC_TESTES_ORM_SQL_CONEXAO = Read-Host -Prompt 'Conexão do banco de testes' -MaskInput   # PowerShell 7+

.EXAMPLE
    ./TEC.ORM.Tests/Database/DbFirst/scaffold.ps1     # executado da pasta TEC.ORM
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($env:TEC_TESTES_ORM_SQL_CONEXAO)) {
    throw 'Defina a variável de ambiente TEC_TESTES_ORM_SQL_CONEXAO (ver a ajuda: Get-Help ./scaffold.ps1 -Full).'
}

$root = Resolve-Path (Join-Path $PSScriptRoot '..\..\..')
Push-Location $root
try {
    dotnet tool restore | Out-Null
    dotnet ef dbcontext scaffold $env:TEC_TESTES_ORM_SQL_CONEXAO Microsoft.EntityFrameworkCore.SqlServer `
        --project TEC.ORM.Tests `
        --framework net10.0 `
        --schema dbfirst `
        --context DbFirstContext `
        --context-dir Database/DbFirst/Generated `
        --output-dir Database/DbFirst/Generated `
        --namespace TEC.ORM.Tests.Database.DbFirst `
        --no-onconfiguring `
        --no-pluralize `
        --force
    if ($LASTEXITCODE -ne 0) { throw "Scaffolding falhou (código $LASTEXITCODE)." }
    Write-Host 'Modelo gerado em Database/DbFirst/Generated. Revise o diff; as partials em Partials\ continuam valendo.'
}
finally {
    Pop-Location
}
