<#
.SYNOPSIS
  Genera datos de prueba realistas en la base de EdenPOS usando la propia logica de la app (AppData).

.DESCRIPTION
  Carga bin\Debug\net48\EdenPOS.exe por reflexion (sin ejecutar la interfaz) y llama a AppData para que stock,
  movimientos de inventario, comprobantes, auditoria y promociones queden coherentes. Como la app fecha todo con la
  hora actual, cada dia de operacion se crea y luego se retrasan sus fechas con UPDATE (por id) para tener historial.

  SE NIEGA a escribir si la base tiene datos reales: exige 0 filas en recibo, cliente, sesion_caja, movimiento_caja,
  ticket_guardado, movimiento_inventario, auditoria (y tablas hijas), un solo usuario (la dueña) y contadores en 1.
  Para regenerar hay que vaciar la base antes (ver DATOS_PRUEBA.md).

  Estos datos son SOLO de prueba: nombres y DNI ficticios (DNI 99000001-99000025), PIN de prueba.
  La facturacion electronica es SIMULADA: no se envia nada a SUNAT.

.PARAMETER Connection
  Cadena de conexion MySqlConnector. Por defecto: MariaDB de XAMPP, 127.0.0.1:3306, base edenpos, root sin contraseña.

.PARAMETER BinDir
  Carpeta con EdenPOS.exe y sus DLL (por defecto ..\..\bin\Debug\net48; compile antes con dotnet build).

.PARAMETER DataPath
  Carpeta temporal para el settings.json local (EDENPOS_DATA_PATH). Si se omite se crea una en %TEMP% y se borra al final.

.PARAMETER Seed
  Semilla del generador aleatorio (misma semilla = mismos datos).
#>
[CmdletBinding()]
param(
    [string]$Connection = 'Server=127.0.0.1;Port=3306;Database=edenpos;User ID=root;Password=;',
    [string]$BinDir = '',
    [string]$DataPath = '',
    [int]$Seed = 20261116
)

$ErrorActionPreference = 'Stop'
$inv = [Globalization.CultureInfo]::InvariantCulture

if (-not $BinDir) { $BinDir = Join-Path $PSScriptRoot '..\..\bin\Debug\net48' }
$BinDir = (Resolve-Path $BinDir).Path
$exePath = Join-Path $BinDir 'EdenPOS.exe'
if (-not (Test-Path $exePath)) { throw "No existe $exePath. Compile el proyecto (dotnet build) antes de generar los datos." }

$ownDataPath = $false
if (-not $DataPath) {
    $DataPath = Join-Path ([IO.Path]::GetTempPath()) ('edenpos-seed-' + [Guid]::NewGuid().ToString('N'))
    $ownDataPath = $true
}
New-Item -ItemType Directory -Force -Path $DataPath | Out-Null
$env:EDENPOS_DATA_PATH = $DataPath
$env:EDENPOS_MYSQL_CONNECTION = $Connection

# ---------------------------------------------------------------------------------------------------------------------
# Carga de ensamblados por bytes (LoadFrom lo bloquea Windows) con un resolvedor que ignora versiones.
# ---------------------------------------------------------------------------------------------------------------------
if (-not ('EdenSeedResolver' -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.IO;
using System.Reflection;
public static class EdenSeedResolver
{
    public static string Dir;
    public static void Install(string dir)
    {
        Dir = dir;
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
    }
    private static Assembly Resolve(object sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name;
        foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
            if (string.Equals(loaded.GetName().Name, name, StringComparison.OrdinalIgnoreCase)) return loaded;
        string path = Path.Combine(Dir, name + ".dll");
        if (File.Exists(path)) return Assembly.Load(File.ReadAllBytes(path));
        return null;
    }
}
"@
}
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
[EdenSeedResolver]::Install($BinDir)
[void][Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $BinDir 'MySqlConnector.dll')))
[void][Reflection.Assembly]::Load([IO.File]::ReadAllBytes($exePath))

# ---------------------------------------------------------------------------------------------------------------------
# Ayudas SQL directas (solo para la comprobacion previa y para retrasar fechas por id)
# ---------------------------------------------------------------------------------------------------------------------
function New-Conn { $c = New-Object MySqlConnector.MySqlConnection($Connection); $c.Open(); return $c }

function Set-Params($cmd, $p) {
    foreach ($k in $p.Keys) {
        $v = $p[$k]
        if ($null -eq $v) { $v = [DBNull]::Value }
        [void]$cmd.Parameters.AddWithValue($k, $v)
    }
}

function Get-Scalar([string]$sql, $p = @{}) {
    $c = New-Conn
    try { $cmd = $c.CreateCommand(); $cmd.CommandText = $sql; Set-Params $cmd $p; return $cmd.ExecuteScalar() }
    finally { $c.Dispose() }
}

function Get-Rows([string]$sql, $p = @{}) {
    $c = New-Conn
    try {
        $cmd = $c.CreateCommand(); $cmd.CommandText = $sql; Set-Params $cmd $p
        $r = $cmd.ExecuteReader()
        try {
            $rows = New-Object System.Collections.ArrayList
            while ($r.Read()) {
                $h = [ordered]@{}
                for ($i = 0; $i -lt $r.FieldCount; $i++) {
                    $v = $r.GetValue($i)
                    if ($v -is [DBNull]) { $v = $null }
                    $h[$r.GetName($i)] = $v
                }
                [void]$rows.Add($h)
            }
            return $rows.ToArray()
        }
        finally { $r.Dispose() }
    }
    finally { $c.Dispose() }
}

function Exec-Sql($c, $tx, [string]$sql, $p = @{}) {
    $cmd = $c.CreateCommand(); $cmd.CommandText = $sql; $cmd.Transaction = $tx
    Set-Params $cmd $p
    [void]$cmd.ExecuteNonQuery()
}

# ---------------------------------------------------------------------------------------------------------------------
# Comprobacion: la base solo debe tener la siembra por defecto
# ---------------------------------------------------------------------------------------------------------------------
function Assert-OnlySeed([bool]$beforeInitialize) {
    $bad = @()
    foreach ($t in 'recibo', 'recibo_linea', 'comprobante', 'cliente', 'sesion_caja', 'movimiento_caja', 'ticket_guardado',
                   'ticket_linea', 'casillero_llave', 'movimiento_inventario', 'auditoria') {
        $n = [int64](Get-Scalar "SELECT COUNT(*) FROM $t")
        if ($n -ne 0) { $bad += "$t=$n filas" }
    }
    $users = @(Get-Rows 'SELECT u.nombre, r.nombre AS rol FROM usuario u LEFT JOIN rol r ON r.id_rol = u.id_rol')
    $maxUsers = 1
    if ($users.Count -gt $maxUsers -or ($users.Count -eq 0 -and -not $beforeInitialize)) { $bad += "usuarios=$($users.Count) (solo debe existir la dueña)" }
    elseif ($users.Count -eq 1 -and $users[0].rol -ne 'Owner') { $bad += 'el unico usuario no es la dueña' }
    $cfg = @(Get-Rows 'SELECT next_receipt_number, next_shift_number FROM configuracion WHERE id = 1')
    if ($cfg.Count -eq 1 -and ($cfg[0].next_receipt_number -ne 1 -or $cfg[0].next_shift_number -ne 1)) { $bad += 'los contadores de recibo/turno no estan en 1' }
    if ($bad.Count -gt 0) {
        throw ("La base NO esta en estado de siembra; no se escribio nada. Detalle: " + ($bad -join '; ') +
               ". Si son datos de prueba anteriores, vacie la base como indica DATOS_PRUEBA.md; si son datos reales, no continue.")
    }
}

Write-Host "Conexion: $($Connection -replace 'Password=[^;]*', 'Password=***')"
Assert-OnlySeed $true
Write-Host 'Comprobacion previa OK: la base solo tiene la siembra por defecto.'

# ---------------------------------------------------------------------------------------------------------------------
# Generador pseudoaleatorio reproducible y ayudas de plan
# ---------------------------------------------------------------------------------------------------------------------
$rng = New-Object System.Random($Seed)
function RInt([int]$a, [int]$b) { return $rng.Next($a, $b + 1) }
function Chance([double]$p) { return ($rng.NextDouble() -lt $p) }
function Pick($arr) { return $arr[$rng.Next(0, $arr.Count)] }
function PickWeighted($pairs) {
    $sum = 0; foreach ($p in $pairs) { $sum += $p[1] }
    $x = $rng.Next(0, $sum)
    foreach ($p in $pairs) { if ($x -lt $p[1]) { return $p[0] }; $x -= $p[1] }
}
function Ln([string]$p, [string]$o, [int]$q) { return @{ p = $p; o = $o; q = $q } }
function Fmt([decimal]$v) { return $v.ToString('0.00', $inv) }

$MidDot = [string][char]0x00B7

# Clientes ficticios: DNI 99000001-99000025 (no existen DNI reales con ese prefijo).
$customerNames = @(
    'MARIBEL TICONA HUANCA', 'JORGE LUIS CONDORI PARI', 'ELVA MAMANI CHOQUE', 'CESAR AUGUSTO FLORES ZEGARRA', 'LUZ MARINA CALIZAYA VARGAS',
    'ALEJANDRO PINTO CASTILLO', 'SOFIA VALDIVIA MEDINA', 'RICARDO APAZA LIMACHE', 'PATRICIA GUTIERREZ SALAS', 'MIGUEL ANGEL CHAMBILLA RUIZ',
    'CARMEN ROSA YUCRA TORRES', 'DIEGO ARMANDO COAQUIRA LUQUE', 'FIORELLA NINA CRUZ', 'HUGO BENITO RAMOS CUTIPA', 'YESENIA ARCE MONTALVO',
    'PABLO ESTEBAN CARPIO VILCA', 'NORMA ISABEL HUANCA TARQUI', 'FERNANDO SALAZAR QUISPE', 'GLADYS MIRIAM TICONA CHURA', 'OSCAR RAUL BERNAL PACO',
    'TANIA LISSETH MAMANI SUCSO', 'VICTORIA APAZA CONDORI', 'EDGAR WILFREDO ARIAS LOZA', 'MILAGROS PAUCAR VELASQUEZ', 'JAVIER ANDRES COLQUE FLORES')
$custName = @{}
$allDni = @()
for ($i = 1; $i -le 25; $i++) {
    $dni = '990000' + $i.ToString('00')
    $custName[$dni] = $customerNames[$i - 1]
    $allDni += $dni
}
$dniA = '99000001'; $dniB = '99000002'; $dniC = '99000003'; $dniD = '99000004'; $dniE = '99000005'
$dniF = '99000006'; $dniG = '99000007'; $dniH = '99000008'; $dniI = '99000009'
$poolDni = @(10..25 | ForEach-Object { '990000' + $_.ToString('00') })

# ---------------------------------------------------------------------------------------------------------------------
# Catalogo de venta
# ---------------------------------------------------------------------------------------------------------------------
$saunaPairs = @(@('GENERAL', 44), @('PAREJA', 9), @('FAMILIA 1', 6), @('FAMILIA 2', 4), @('AMIGOS X3', 4), @('AMIGOS X4', 4), @('AMIGOS X5', 2),
                @('ADULTO MAYOR', 7), @('VIP', 6), @('MUJER 2', 5), @('MUJER 3', 2), @('MUJER 4', 2))
$rentalPairs = @(@('ALQ TOALLA', 35), @('ALQ SANDALIA', 20), @('ALQ SHORT', 15), @('ALQ BIKINI MUJER', 12), @('PAREO', 8), @('ALQ GORRO', 5), @('ALQ LENTES', 3), @('ALQ FLOTADOR', 2))
$cafePool = @(
    @('HIDRATANTE', 'CIELO / 625 ML'), @('HIDRATANTE', 'CIELO / 625 ML'), @('HIDRATANTE', 'CIELO / 1 L'), @('HIDRATANTE', 'VOLT / 300 ML'), @('HIDRATANTE', 'SPORADE / 500 ML'),
    @('HIDRATANTE', 'SPORADE / 1 L'), @('HIDRATANTE', 'CIELO / 2.5 L'),
    @('GASEOSAS', 'INCA COLA / 600 ML'), @('GASEOSAS', 'COCA COLA / 600 ML'), @('GASEOSAS', 'COCA COLA / 2.5 L'), @('GASEOSAS', 'INCA COLA / 2.5 L'),
    @('SNACKS', 'PAPRIKA'), @('SNACKS', 'TENTACIÓN'), @('SNACKS', 'NIK'), @('SNACKS', 'OREO'), @('SNACKS', 'RITZ'),
    @('SANDWICHES', 'TOSTADA DE QUESO'), @('SANDWICHES', 'SÁNDWICH DE POLLO'), @('SANDWICHES', 'CHORIPÁN'),
    @('JARRAS', 'LIMONADA / 1 L'), @('JARRAS', 'MARACUYÁ / 1.5 L'), @('JARRAS', 'PIÑA / 1.5 L'), @('MARCIANOS', ''), @('MARCIANOS', ''))
$aseoPool = @(
    @('JABONES', 'HOTELERO'), @('JABONES', 'FRUTAL'), @('JABONES', 'REXONA'), @('SHAMPOOS', 'H&S'), @('SHAMPOOS', 'H&S MINI'), @('SHAMPOOS', 'SEDAL'),
    @('SHAMPOOS', 'PANTENE'), @('ACONDICIONADOR', 'BONAWELL'), @('DESODORANTE', ''), @('BLOQUEADOR', ''), @('AFEITADOR', ''), @('GORROS', ''),
    @('ESPONJA DE BAÑO', ''), @('BICARBONATO', ''), @('LENTES', ''), @('GANCHOS CABELLO', ''))
$methodPairs = @(@('Efectivo', 55), @('Yape', 30), @('Mixto', 15))

# Articulos que controlan inventario: minimo, y los que quedan por debajo del minimo (low = stock final) para probar el rojo.
$trackList = New-Object System.Collections.ArrayList
function Add-Track([string]$p, [string]$o, [int]$min, $low = $null, [bool]$restock = $false) {
    [void]$trackList.Add(@{ p = $p; o = $o; min = $min; low = $low; restock = $restock; key = ($p + '|' + $o) })
}
Add-Track 'AFEITADOR' '' 6; Add-Track 'BICARBONATO' '' 6; Add-Track 'BLOQUEADOR' '' 4; Add-Track 'DESODORANTE' '' 5 0
Add-Track 'ESPONJA DE BAÑO' '' 6; Add-Track 'GANCHOS CABELLO' '' 10; Add-Track 'GORROS' '' 10; Add-Track 'LENTES' '' 4; Add-Track 'MARCIANOS' '' 12 $null $true
Add-Track 'ACONDICIONADOR' 'BONAWELL' 8; Add-Track 'ACONDICIONADOR' 'NUTRIBELA' 8
Add-Track 'GASEOSAS' 'INCA COLA / 600 ML' 12 $null $true; Add-Track 'GASEOSAS' 'INCA COLA / 2.5 L' 6 2
Add-Track 'GASEOSAS' 'COCA COLA / 600 ML' 12 $null $true; Add-Track 'GASEOSAS' 'COCA COLA / 2.5 L' 6
Add-Track 'HIDRATANTE' 'CIELO / 625 ML' 15 $null $true; Add-Track 'HIDRATANTE' 'CIELO / 1 L' 8; Add-Track 'HIDRATANTE' 'CIELO / 2.5 L' 6
Add-Track 'HIDRATANTE' 'VOLT / 300 ML' 8; Add-Track 'HIDRATANTE' 'SPORADE / 1 L' 6; Add-Track 'HIDRATANTE' 'SPORADE / 500 ML' 8
Add-Track 'JABONES' 'HOTELERO' 15; Add-Track 'JABONES' 'FRUTAL' 10; Add-Track 'JABONES' 'REXONA' 6; Add-Track 'JABONES' 'PALMOLIVE' 6
Add-Track 'SHAMPOOS' 'H&S' 10; Add-Track 'SHAMPOOS' 'NUTRIBELA' 10; Add-Track 'SHAMPOOS' 'PANTENE' 10; Add-Track 'SHAMPOOS' 'SEDAL' 10
Add-Track 'SHAMPOOS' 'AMARAS' 10; Add-Track 'SHAMPOOS' 'H&S MINI' 12
Add-Track 'SNACKS' 'PAPRIKA' 15 $null $true; Add-Track 'SNACKS' 'TENTACIÓN' 15; Add-Track 'SNACKS' 'NIK' 15; Add-Track 'SNACKS' 'OREO' 15; Add-Track 'SNACKS' 'RITZ' 10 4
$trackByKey = @{}
foreach ($t in $trackList) { $trackByKey[$t.key] = $t }

# ---------------------------------------------------------------------------------------------------------------------
# Construccion de ventas
# ---------------------------------------------------------------------------------------------------------------------
function Merge-Specs($specs) {
    $merged = New-Object System.Collections.ArrayList
    foreach ($s in $specs) {
        $found = $null
        foreach ($m in $merged) { if ($m.p -eq $s.p -and $m.o -eq $s.o) { $found = $m; break } }
        if ($found) { $found.q += $s.q } else { [void]$merged.Add(@{ p = $s.p; o = $s.o; q = $s.q }) }
    }
    return , $merged.ToArray()
}

function New-Sale($specs, [string]$dni = '', [string]$method = '') {
    if (-not $method) { $method = PickWeighted $methodPairs }
    return @{ lines = (Merge-Specs $specs); dni = $dni; method = $method; refund = $null; reject = $false; cashFixed = $null }
}

function Add-Rentals($specs, [int]$people) {
    $n = RInt 1 2
    for ($i = 0; $i -lt $n; $i++) { [void]$specs.Add((Ln (PickWeighted $rentalPairs) '' (RInt 1 ([math]::Max(1, [math]::Min($people, 4)))))) }
}
function Add-Cafe($specs, [int]$max) {
    $n = RInt 1 $max
    for ($i = 0; $i -lt $n; $i++) { $it = Pick $cafePool; [void]$specs.Add((Ln $it[0] $it[1] (RInt 1 3))) }
}
function Add-Aseo($specs, [int]$max) {
    $n = RInt 1 $max
    for ($i = 0; $i -lt $n; $i++) { $it = Pick $aseoPool; [void]$specs.Add((Ln $it[0] $it[1] (RInt 1 2))) }
}

function New-RandomSale {
    $r = $rng.NextDouble()
    $specs = New-Object System.Collections.ArrayList
    $dni = ''
    if ($r -lt 0.72) {
        $main = PickWeighted $saunaPairs
        $q = 1
        if ($main -eq 'GENERAL') {
            $q = PickWeighted @(@(1, 55), @(2, 30), @(3, 15))
            if (Chance 0.07) { $q = RInt 4 6 }
        }
        elseif (Chance 0.10) { $q = 2 }
        [void]$specs.Add((Ln $main '' $q))
        if ($main -eq 'GENERAL' -and (Chance 0.25)) { [void]$specs.Add((Ln 'NIÑO' '' (RInt 1 2))) }
        if (Chance 0.03) { [void]$specs.Add((Ln 'PASE CORTESÍA' '' 1)) }
        if (Chance 0.45) { Add-Rentals $specs $q }
        if (Chance 0.45) { Add-Cafe $specs 2 }
        if (Chance 0.12) { Add-Aseo $specs 1 }
        if (Chance 0.35) { $dni = Pick $poolDni }
    }
    elseif ($r -lt 0.86) { Add-Cafe $specs 3 }
    elseif ($r -lt 0.94) { Add-Rentals $specs 2; if (Chance 0.4) { Add-Cafe $specs 1 } }
    else { Add-Aseo $specs 2 }
    return (New-Sale $specs $dni)
}

# Ventas guionadas por dia (offset respecto a hoy: -14 .. 0)
$scripted = @{}
foreach ($o in -14..0) { $scripted[$o] = New-Object System.Collections.ArrayList }
function Script-Sale([int]$off, $specs, [string]$dni = '', [string]$method = '', $refund = $null, [bool]$reject = $false, $cashFixed = $null) {
    $s = New-Sale $specs $dni $method
    $s.refund = $refund; $s.reject = $reject; $s.cashFixed = $cashFixed
    [void]$scripted[$off].Add($s)
}

# B: cliente frecuente: 12 visitas seguidas (la 6a y la 12a son gratis) y la 13a hoy, que se cobra.
foreach ($o in -14..-3) { Script-Sale $o @((Ln 'GENERAL' '' 1), (Ln 'HIDRATANTE' 'CIELO / 1 L' 1)) $dniB (Pick @('Efectivo', 'Yape')) }
Script-Sale 0 @((Ln 'GENERAL' '' 1), (Ln 'HIDRATANTE' 'CIELO / 1 L' 1)) $dniB 'Efectivo'
# A: 5 visitas pagadas y la 6a gratis (dia -4); la 7a se cobra.
foreach ($o in -14, -12, -10, -8, -6, -4, -2) {
    $sp = @((Ln 'GENERAL' '' 1), (Ln 'ALQ TOALLA' '' 1))
    if ($o -eq -4) { $sp += (Ln 'GASEOSAS' 'COCA COLA / 600 ML' 1) }
    Script-Sale $o $sp $dniA 'Efectivo'
}
# C: 5 visitas pagadas -> su PROXIMA visita es gratis. D: 3 visitas.
foreach ($o in -13, -10, -7, -4, -1) { Script-Sale $o @((Ln 'ADULTO MAYOR' '' 1), (Ln 'SNACKS' 'OREO' 1)) $dniC 'Efectivo' }
foreach ($o in -12, -6, -2) { Script-Sale $o @((Ln 'PAREJA' '' 1), (Ln 'GASEOSAS' 'COCA COLA / 600 ML' 2)) $dniD 'Yape' }
# Grupos de 4 o mas personas (GRUPAL).
Script-Sale -12 @((Ln 'GENERAL' '' 2), (Ln 'NIÑO' '' 3), (Ln 'ALQ FLOTADOR' '' 2)) '' 'Efectivo'
Script-Sale -10 @((Ln 'AMIGOS X4' '' 1), (Ln 'HIDRATANTE' 'CIELO / 625 ML' 2)) '' 'Efectivo'
Script-Sale -7 @((Ln 'AMIGOS X5' '' 1), (Ln 'JARRAS' 'LIMONADA / 2 L' 1)) '' 'Yape'
Script-Sale -4 @((Ln 'FAMILIA 2' '' 2), (Ln 'SANDWICHES' 'TOSTADA DE QUESO' 4), (Ln 'GASEOSAS' 'COCA COLA / 2.5 L' 1)) '' 'Mixto'
Script-Sale -2 @((Ln 'GENERAL' '' 6)) '' 'Efectivo'
Script-Sale -1 @((Ln 'MUJER 4' '' 1), (Ln 'JARRAS' 'MARACUYÁ / 2 L' 1)) '' 'Efectivo'
# Venta grande (> S/ 700 con DNI): 10 x AMIGOS X5 + consumo; el grupal 10% la deja en 748.80.
Script-Sale -9 @((Ln 'AMIGOS X5' '' 10), (Ln 'JARRAS' 'PIÑA / 2 L' 4), (Ln 'SANDWICHES' 'CHORIPÁN' 10)) $dniF 'Mixto' $null $false ([decimal]400)
# Reembolsos (3) con motivo.
Script-Sale -11 @((Ln 'GASEOSAS' 'COCA COLA / 600 ML' 2), (Ln 'SNACKS' 'OREO' 2), (Ln 'SANDWICHES' 'SÁNDWICH DE POLLO' 1)) $dniH 'Efectivo' 'Gaseosas entregadas en mal estado; se devuelve el importe'
Script-Sale -6 @((Ln 'GENERAL' '' 2)) '' 'Efectivo' 'Error de cobro: los clientes no llegaron a ingresar'
Script-Sale -3 @((Ln 'PAREJA' '' 1), (Ln 'ALQ TOALLA' '' 2), (Ln 'MARCIANOS' '' 2)) '' 'Yape' 'Reclamo por falla del vapor; se devuelve el importe'
# Comprobantes que el emisor SIMULADO rechaza (2).
Script-Sale -5 @((Ln 'GENERAL' '' 1), (Ln 'ALQ TOALLA' '' 1)) $dniI 'Yape' $null $true
Script-Sale -2 @((Ln 'VIP' '' 1), (Ln 'HIDRATANTE' 'SPORADE / 500 ML' 1)) '' 'Efectivo' $null $true
# Desodorante: queda agotado (stock 0, minimo 5).
Script-Sale -13 @((Ln 'DESODORANTE' '' 1), (Ln 'JABONES' 'HOTELERO' 1)) '' 'Efectivo'
Script-Sale -8 @((Ln 'DESODORANTE' '' 2)) '' 'Yape'
Script-Sale -3 @((Ln 'DESODORANTE' '' 1), (Ln 'SHAMPOOS' 'H&S MINI' 1)) '' 'Efectivo'

# Dias: turno, ventas, movimientos de caja y cierre
$now = Get-Date
$today = $now.Date
$closeDiff = @{ -12 = [decimal]-5.00; -7 = [decimal]3.50; -3 = [decimal]-12.00 }
$moveSalida = @(@('Compra de insumos de limpieza', 45.50), @('Compra de hielo y vasos', 28.00), @('Pasajes de personal', 20.00),
                @('Pago a proveedor de gaseosas', 120.00), @('Compra de gas para la caldera', 65.00), @('Compra de papel higienico y bolsas', 32.50))
$moveDeposito = @(@('Deposito de caja chica', 50.00), @('Cambio en monedas', 40.00), @('Aporte para vueltos', 60.00))

$plan = @()
foreach ($off in -14..0) {
    $date = $today.AddDays($off)
    $weekend = ($date.DayOfWeek.ToString() -in 'Saturday', 'Sunday')
    if ($off -eq 0) { $n = 6 } elseif ($weekend) { $n = RInt 15 20 } else { $n = RInt 8 12 }
    $sales = New-Object System.Collections.ArrayList
    foreach ($s in $scripted[$off]) { [void]$sales.Add($s) }
    while ($sales.Count -lt $n) { [void]$sales.Add((New-RandomSale)) }
    $sales = @($sales.ToArray() | Sort-Object { $rng.Next() })
    $d = @{ off = $off; date = $date; worker = $(if ([math]::Abs($off) % 2 -eq 0) { 'CARLOS' } else { 'MARIA' }); sales = $sales; isToday = ($off -eq 0) }
    $d.opening = [decimal](Pick @(50, 80, 100, 100, 120, 150))
    if ($off -eq 0) {
        $d.open = $now.AddHours(-3)
        if ($d.open -lt $today.AddMinutes(1)) { $d.open = $today.AddMinutes(1) }
        $d.open = $d.open.AddSeconds(-$d.open.Second)
        $d.close = $null
        $spanStart = $d.open.AddMinutes(8); $spanEnd = $now.AddMinutes(-8)
        if ($spanEnd -le $spanStart) { throw 'Es demasiado temprano para generar el turno de hoy (la hora actual no deja margen tras la apertura). Ejecute el script mas tarde.' }
    }
    else {
        $d.open = $date.AddHours(10).AddMinutes((RInt 0 25)).AddSeconds((RInt 0 59))
        $d.close = $date.AddHours(20).AddMinutes((RInt 0 40)).AddSeconds((RInt 0 59))
        $spanStart = $d.open.AddMinutes(8); $spanEnd = $d.close.AddMinutes(-25)
    }
    $totalSecs = [int]($spanEnd - $spanStart).TotalSeconds
    $secs = @(1..$sales.Count | ForEach-Object { $rng.Next(0, $totalSecs) } | Sort-Object)
    for ($i = 0; $i -lt $sales.Count; $i++) { $sales[$i].time = $spanStart.AddSeconds($secs[$i]) }
    # Movimientos de caja (1-2), dentro del turno.
    $moves = @()
    $ms = Pick $moveSalida
    $mt = $d.open.AddMinutes((RInt 60 200))
    if ($off -eq 0) { $mt = $d.open.AddMinutes((RInt 25 70)) }
    $moves += @{ type = 'Salida'; amount = [decimal]$ms[1]; comment = $ms[0]; time = $mt }
    if (Chance 0.5) {
        $md = Pick $moveDeposito
        $mt2 = $mt.AddMinutes((RInt 20 120))
        if ($off -eq 0) { $mt2 = $d.open.AddMinutes((RInt 75 110)) }
        $moves += @{ type = 'Depósito'; amount = [decimal]$md[1]; comment = $md[0]; time = $mt2 }
    }
    if ($off -eq 0) { foreach ($mv in $moves) { if ($mv.time -gt $now.AddMinutes(-1)) { $mv.time = $now.AddMinutes(-1) } } }
    $d.moves = $moves
    $d.diff = $(if ($closeDiff.ContainsKey($off)) { $closeDiff[$off] } else { [decimal]0 })
    $plan += $d
}

# Consumo planificado de articulos con inventario (para dimensionar las entradas de stock iniciales).
$gross = @{}; $refunded = @{}; $cons1 = @{}
$restockOffset = -7
foreach ($d in $plan) {
    foreach ($s in $d.sales) {
        foreach ($l in $s.lines) {
            $k = $l.p + '|' + $l.o
            if (-not $trackByKey.ContainsKey($k)) { continue }
            if (-not $gross.ContainsKey($k)) { $gross[$k] = 0; $refunded[$k] = 0; $cons1[$k] = 0 }
            $gross[$k] += $l.q
            if ($s.refund) {
                if ($trackByKey[$k].low -ne $null) { throw "Una venta reembolsada usa el articulo $k, que queda bajo el minimo." }
                $refunded[$k] += $l.q
            }
            if ($d.off -lt $restockOffset) { $cons1[$k] += $l.q }
        }
    }
}

# ---------------------------------------------------------------------------------------------------------------------
# Ayudas de la app
# ---------------------------------------------------------------------------------------------------------------------
function Get-User([string]$name) {
    foreach ($u in [EdenPOS.AppData]::Users) { if ($u.Name -eq $name) { return $u } }
    throw "No existe el usuario $name."
}
function Sign-In([string]$name) { [EdenPOS.AppSession]::SignIn((Get-User $name)) }

$ProdByName = @{}
function Load-Products {
    $script:ProdByName = @{}
    foreach ($p in [EdenPOS.AppData]::Products) { $script:ProdByName[$p.Name] = $p }
}

function Build-Lines($specs) {
    $list = New-Object 'System.Collections.Generic.List[EdenPOS.ReceiptLine]'
    foreach ($s in $specs) {
        $prod = $ProdByName[$s.p]
        if (-not $prod) { throw "El articulo '$($s.p)' no existe en la base." }
        $line = New-Object EdenPOS.ReceiptLine
        $line.ProductId = $prod.Id
        if ($s.o) {
            $opt = $null
            foreach ($x in $prod.Options) { if ($x.Name -eq $s.o) { $opt = $x } }
            if (-not $opt) { throw "La variante '$($s.o)' de '$($s.p)' no existe." }
            $line.OptionId = $opt.Id
            $line.Name = $prod.Name + ' ' + $MidDot + ' ' + $opt.Name
            $line.UnitPrice = [decimal]$opt.Price
        }
        else {
            $line.Name = $prod.Name
            $line.UnitPrice = [decimal]$prod.Price
        }
        $line.Quantity = [int]$s.q
        $list.Add($line)
    }
    return , $list
}

function Wait-Invoices {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ([EdenPOS.AppData]::GetComprobantesResumen().Pendiente -gt 0) {
        if ($sw.Elapsed.TotalSeconds -gt 60) { throw 'Los comprobantes simulados siguen Pendiente despues de 60 s.' }
        Start-Sleep -Milliseconds 120
    }
}

function Get-Payment([decimal]$total, [string]$method, $cashFixed) {
    $received = $total; $cash = [decimal]-1; $yape = [decimal]-1
    if ($method -eq 'Efectivo') {
        $step = [decimal](Pick @(1, 5, 10, 20, 50))
        $received = [math]::Ceiling($total / $step) * $step
        if ($received -lt $total) { $received = $total }
    }
    elseif ($method -eq 'Mixto') {
        if ($null -ne $cashFixed) { $cash = [decimal]$cashFixed }
        else { $cash = [math]::Floor($total * [decimal](RInt 30 70) / 100); if ($cash -lt 1) { $cash = [decimal]1 } }
        if ($cash -ge $total) { return @{ method = 'Efectivo'; received = $total; cash = [decimal]-1; yape = [decimal]-1 } }
        $yape = $total - $cash
        $received = $cash
    }
    return @{ method = $method; received = $received; cash = $cash; yape = $yape }
}

# ---------------------------------------------------------------------------------------------------------------------
# Escritura
# ---------------------------------------------------------------------------------------------------------------------
$summary = [ordered]@{}
try {
    [EdenPOS.AppData]::Initialize()
    Assert-OnlySeed $false

    $owner = $null
    foreach ($u in [EdenPOS.AppData]::Users) { if ($u.Role -eq 'Owner') { $owner = $u } }
    [EdenPOS.AppSession]::SignIn($owner)
    $ownerName = $owner.Name
    $day0 = $today.AddDays(-15)

    # --- Configuracion: promociones activas (sexta visita, grupal desde 4 personas, 10%) -------------------------------
    $cfg = [EdenPOS.AppData]::Settings
    $cfg.PromoSextaVisitaActiva = $true
    $cfg.PromoGrupalMinimoPersonas = 4
    $cfg.PromoGrupalPorcentaje = [decimal]10
    [EdenPOS.AppData]::SaveSettings($cfg)
    [EdenPOS.AppData]::SimularRechazoComprobantes = $false

    # --- Usuarios --------------------------------------------------------------------------------------------------------
    foreach ($u in @(@('CARLOS', '2468'), @('MARIA', '1357'), @('PEDRO', '9753'))) {
        [void][EdenPOS.AppData]::SaveUser($null, $u[0], $u[1], 'Worker')
    }
    Write-Host 'Usuarios creados: CARLOS, MARIA, PEDRO (PIN de prueba en DATOS_PRUEBA.md).'

    # --- Clientes ----------------------------------------------------------------------------------------------------------
    foreach ($dni in $allDni) { [void][EdenPOS.AppData]::SaveCustomer($dni, $custName[$dni], 'Manual') }
    Write-Host "Clientes creados: $($allDni.Count)."

    # --- Inventario: entradas iniciales y minimos ------------------------------------------------------------------------
    Load-Products
    $restockPending = New-Object System.Collections.ArrayList
    $finalWanted = @{}
    foreach ($t in $trackList) {
        $k = $t.key
        $g = $(if ($gross.ContainsKey($k)) { [int]$gross[$k] } else { 0 })
        $rf = $(if ($refunded.ContainsKey($k)) { [int]$refunded[$k] } else { 0 })
        $c1 = $(if ($cons1.ContainsKey($k)) { [int]$cons1[$k] } else { 0 })
        $final = $(if ($t.low -ne $null) { [int]$t.low } else { $t.min + (RInt 4 22) })
        $finalWanted[$k] = $final
        $total = $g - $rf + $final
        if ($total -le 0) { $total = [math]::Max(1, $final) }
        $first = $total
        $second = 0
        if ($t.restock) {
            $try = $c1 + (RInt 6 14)
            if ($try -lt $total - 5) { $first = $try; $second = $total - $try }
        }
        $prod = $ProdByName[$t.p]
        if (-not $prod) { throw "No existe el articulo $($t.p)." }
        $optId = $null
        if ($t.o) {
            foreach ($x in $prod.Options) { if ($x.Name -eq $t.o) { $optId = $x.Id } }
            if (-not $optId) { throw "No existe la variante $($t.o) de $($t.p)." }
            [EdenPOS.AppData]::AddInventoryEntry($prod.Id, $optId, [int]$first, 'Stock inicial (compra a proveedor)')
        }
        else { [EdenPOS.AppData]::AddInventoryEntry($prod.Id, [int]$first, 'Stock inicial (compra a proveedor)') }
        if ($second -gt 0) { [void]$restockPending.Add(@{ prodId = $prod.Id; optId = $optId; qty = [int]$second; key = $k }) }
    }
    # Minimos de stock (SaveProducts no cambia precios, asi que no genera auditoria).
    $plist = [EdenPOS.AppData]::Products
    foreach ($t in $trackList) {
        foreach ($p in $plist) {
            if ($p.Name -ne $t.p) { continue }
            if ($t.o) { foreach ($x in $p.Options) { if ($x.Name -eq $t.o) { $x.StockMinimo = [int]$t.min } } }
            else { $p.StockMinimo = [int]$t.min }
        }
    }
    [EdenPOS.AppData]::SaveProducts($plist)
    # Fechas de la siembra: todo anterior al primer dia de operacion.
    $c = New-Conn; $tx = $c.BeginTransaction()
    try {
        $seq = 0
        foreach ($row in @(Get-Rows "SELECT id_movimiento FROM movimiento_inventario ORDER BY secuencia")) {
            Exec-Sql $c $tx 'UPDATE movimiento_inventario SET created_at = @t WHERE id_movimiento = @id' @{ '@t' = $day0.AddHours(8).AddSeconds(20 * $seq); '@id' = $row.id_movimiento }
            $seq++
        }
        Exec-Sql $c $tx 'UPDATE usuario SET created_at = @t, updated_at = @t WHERE nombre = @n' @{ '@t' = $day0.AddDays(-5).AddHours(8); '@n' = $ownerName }
        $k2 = 0
        foreach ($nm in 'CARLOS', 'MARIA', 'PEDRO') {
            Exec-Sql $c $tx 'UPDATE usuario SET created_at = @t, updated_at = @t WHERE nombre = @n' @{ '@t' = $day0.AddHours(8).AddMinutes(20 + 5 * $k2); '@n' = $nm }
            $k2++
        }
        $tx.Commit()
    }
    catch { $tx.Rollback(); throw }
    finally { $c.Dispose() }
    Write-Host "Inventario inicial cargado ($($trackList.Count) articulos/variantes con control de stock)."

    # --- Dias de operacion ------------------------------------------------------------------------------------------------
    $allDone = @()
    $deactivateTime = $null
    $recNumbers = @{ refunded = @(); rejected = @() }
    foreach ($d in $plan) {
        $off = $d.off
        $date = $d.date
        $auditBefore = [int64](Get-Scalar 'SELECT COALESCE(MAX(id_auditoria), 0) FROM auditoria')
        $invBefore = [int64](Get-Scalar 'SELECT COALESCE(MAX(secuencia), 0) FROM movimiento_inventario')
        $ownerTimes = @{}
        $entryTime = $d.open.AddMinutes(-35)

        # Tareas de la dueña al inicio del dia.
        [EdenPOS.AppSession]::SignIn($owner)
        if ($off -eq -9) {
            [EdenPOS.AppData]::SetUserActive((Get-User 'PEDRO').Id, $false)
            $deactivateTime = $date.AddHours(9).AddMinutes(40)
            $ownerTimes['USUARIO_DESACTIVADO'] = $deactivateTime
        }
        if ($off -eq -8) {
            $pl = [EdenPOS.AppData]::Products
            foreach ($p in $pl) { if ($p.Name -eq 'MARCIANOS') { $p.Price = [decimal]2.50 } }
            [EdenPOS.AppData]::SaveProducts($pl)
            $ownerTimes['PRECIO_CAMBIADO'] = $date.AddHours(9).AddMinutes(45)
        }
        if ($off -eq $restockOffset) {
            foreach ($rs in $restockPending) {
                if ($rs.optId) { [EdenPOS.AppData]::AddInventoryEntry($rs.prodId, $rs.optId, [int]$rs.qty, 'Reposicion de stock') }
                else { [EdenPOS.AppData]::AddInventoryEntry($rs.prodId, [int]$rs.qty, 'Reposicion de stock') }
            }
        }
        if ($off -eq -4) {
            $manoId = $null
            foreach ($p in [EdenPOS.AppData]::Products) { if ($p.Name -eq 'MANO SAUNA') { $manoId = $p.Id } }
            if ($manoId) { [EdenPOS.AppData]::DeleteProduct($manoId); $ownerTimes['PRODUCTO_ELIMINADO'] = $date.AddHours(9).AddMinutes(50) }
        }

        # Turno del trabajador.
        Sign-In $d.worker
        Load-Products
        $shift = [EdenPOS.AppData]::OpenNewShift($d.opening)
        $d.shiftId = $shift.Id
        $done = @()
        $moveIds = @()
        $half = [math]::Floor($d.sales.Count / 2)
        $idx = 0
        foreach ($s in $d.sales) {
            $lines = Build-Lines $s.lines
            $dni = $s.dni
            $cname = $(if ($dni) { $custName[$dni] } else { '' })
            $promo = [EdenPOS.AppData]::CalculatePromotion($dni, $lines)
            $sub = [decimal]0; foreach ($l in $lines) { $sub += $l.UnitPrice * $l.Quantity }
            $total = [math]::Round($sub, 2) - $promo.Descuento
            $pay = Get-Payment $total $s.method $s.cashFixed
            if ($s.reject) { Wait-Invoices; [EdenPOS.AppData]::SimularRechazoComprobantes = $true }
            try {
                $r = [EdenPOS.AppData]::AddReceipt($lines, $cname, $dni, $pay.method, [decimal]$pay.received, [decimal]$pay.cash, [decimal]$pay.yape, [decimal]$promo.Descuento, $promo.PromocionId)
                if ($s.reject) { Wait-Invoices }
            }
            finally { if ($s.reject) { [EdenPOS.AppData]::SimularRechazoComprobantes = $false } }
            $rec = @{ receipt = $r; id = $r.Id; number = $r.Number; time = $s.time; sale = $s; refundTime = $null; promo = $r.PromotionId; total = $r.Total }
            $done += $rec
            if ($s.reject) { $recNumbers.rejected += $r.Number }
            $idx++
            if ($idx -eq $half) {
                foreach ($m in $d.moves) {
                    $cm = [EdenPOS.AppData]::AddCashMovement($m.type, [decimal]$m.amount, $m.comment)
                    $m.id = $cm.Id
                }
            }
        }
        if ($d.moves.Count -gt 0 -and -not $d.moves[0].id) {
            foreach ($m in $d.moves) { $cm = [EdenPOS.AppData]::AddCashMovement($m.type, [decimal]$m.amount, $m.comment); $m.id = $cm.Id }
        }
        Wait-Invoices
        # Reembolsos del dia.
        foreach ($rec in $done) {
            if (-not $rec.sale.refund) { continue }
            [EdenPOS.AppData]::RefundReceipt($rec.receipt, $rec.sale.refund)
            $limit = $(if ($d.close) { $d.close.AddMinutes(-3) } else { $now.AddMinutes(-2) })
            $rt = $rec.time.AddMinutes((RInt 12 40))
            if ($rt -gt $limit) { $rt = $limit }
            $rec.refundTime = $rt
            $recNumbers.refunded += $rec.number
        }
        # Tickets guardados (hoy, a nombre de CARLOS).
        if ($d.isToday) {
            $t1 = Build-Lines (Merge-Specs @((Ln 'PAREJA' '' 1), (Ln 'HIDRATANTE' 'CIELO / 1 L' 2)))
            $tk1 = [EdenPOS.AppData]::SaveTicket($t1, $custName[$dniE], $dniE, 'Pareja llaves 3 y 7', [int[]]@(3, 7), 2, 0)
            $t2 = Build-Lines (Merge-Specs @((Ln 'GENERAL' '' 1), (Ln 'ALQ TOALLA' '' 1), (Ln 'SNACKS' 'NIK' 1)))
            $tk2 = [EdenPOS.AppData]::SaveTicket($t2, $custName[$dniG], $dniG, 'Cliente llave 12', [int[]]@(12), 1, 0)
            $d.tickets = @(@{ id = $tk1.Id; time = $now.AddMinutes(-28) }, @{ id = $tk2.Id; time = $now.AddMinutes(-9) })
        }
        # Cierre del turno (todos menos hoy), con diferencia en 3 dias.
        if (-not $d.isToday) {
            $open = [EdenPOS.AppData]::OpenShift
            $expected = [EdenPOS.AppData]::CalculateExpectedCash($open)
            # RF-05: el Yape confirmado cuadra con el esperado; RN-09: con diferencia de efectivo la observacion es obligatoria.
            $expectedYape = [EdenPOS.AppData]::CalculateExpectedYape($open)
            $note = $(if ($d.diff -ne 0) { 'Diferencia de prueba al contar' } else { $null })
            [void][EdenPOS.AppData]::CloseShift($open.Id, $expected + $d.diff, $expectedYape, $note)
        }
        Wait-Invoices

        # --- Retrasar fechas de lo creado en este dia (por id) --------------------------------------------------------
        $c = New-Conn; $tx = $c.BeginTransaction()
        try {
            Exec-Sql $c $tx 'UPDATE sesion_caja SET fecha_apertura = @a, fecha_cierre = @c WHERE id_sesion = @id' @{ '@a' = $d.open; '@c' = $d.close; '@id' = $d.shiftId }
            foreach ($m in $d.moves) { Exec-Sql $c $tx 'UPDATE movimiento_caja SET created_at = @t WHERE id_movimiento = @id' @{ '@t' = $m.time; '@id' = $m.id } }
            $byId = @{}; $byNum = @{}
            foreach ($rec in $done) {
                $byId[$rec.id] = $rec; $byNum[[string]$rec.number] = $rec
                Exec-Sql $c $tx 'UPDATE recibo SET created_at = @t, refunded_at = CASE WHEN estado = ''Reembolsado'' THEN @rt ELSE NULL END WHERE id_recibo = @id' @{ '@t' = $rec.time; '@rt' = $rec.refundTime; '@id' = $rec.id }
                Exec-Sql $c $tx 'UPDATE comprobante SET fecha_emision = @t, ultimo_intento = @u WHERE id_recibo = @id' @{ '@t' = $rec.time; '@u' = $rec.time.AddSeconds(1); '@id' = $rec.id }
            }
            $k3 = 0
            foreach ($row in @(Get-Rows 'SELECT id_movimiento, tipo, referencia_id FROM movimiento_inventario WHERE secuencia > @b ORDER BY secuencia' @{ '@b' = $invBefore })) {
                $t = $null
                if ($row.tipo -eq 'Venta' -and $byId.ContainsKey($row.referencia_id)) { $t = $byId[$row.referencia_id].time }
                elseif ($row.tipo -eq 'Reembolso' -and $byId.ContainsKey($row.referencia_id)) { $t = $byId[$row.referencia_id].refundTime }
                elseif ($row.tipo -eq 'Entrada') { $t = $entryTime.AddSeconds(15 * $k3); $k3++ }
                if ($null -eq $t) { throw "Movimiento de inventario sin fecha asignada ($($row.tipo))." }
                Exec-Sql $c $tx 'UPDATE movimiento_inventario SET created_at = @t WHERE id_movimiento = @id' @{ '@t' = $t; '@id' = $row.id_movimiento }
            }
            foreach ($row in @(Get-Rows 'SELECT id_auditoria, accion, detalle FROM auditoria WHERE id_auditoria > @b ORDER BY id_auditoria' @{ '@b' = $auditBefore })) {
                $t = $null
                switch ($row.accion) {
                    'RECIBO_REEMBOLSADO' { if ($row.detalle -match '^Recibo #(\d+),') { $t = $byNum[$Matches[1]].refundTime } }
                    'COMPROBANTE_RECHAZADO' { if ($row.detalle -match 'recibo #(\d+)\)') { $t = $byNum[$Matches[1]].time.AddSeconds(1) } }
                    'CAJA_CERRADA' { $t = $d.close }
                    'CAJA_DIFERENCIA' { $t = $d.close }
                    default { if ($ownerTimes.ContainsKey($row.accion)) { $t = $ownerTimes[$row.accion] } }
                }
                if ($null -eq $t) { throw "Fila de auditoria sin fecha asignada ($($row.accion): $($row.detalle))." }
                Exec-Sql $c $tx 'UPDATE auditoria SET created_at = @t WHERE id_auditoria = @id' @{ '@t' = $t; '@id' = $row.id_auditoria }
            }
            if ($d.isToday) {
                foreach ($tk in $d.tickets) { Exec-Sql $c $tx 'UPDATE ticket_guardado SET created_at = @t, updated_at = @t WHERE id_ticket = @id' @{ '@t' = $tk.time; '@id' = $tk.id } }
            }
            $tx.Commit()
        }
        catch { $tx.Rollback(); throw }
        finally { $c.Dispose() }
        $allDone += $done
        Write-Host ("Dia {0} ({1}) turno de {2}: {3} ventas{4}" -f $date.ToString('yyyy-MM-dd'), $date.DayOfWeek, $d.worker, $done.Count, $(if ($d.isToday) { ' (turno ABIERTO)' } else { '' }))
    }

    # --- Fechas finales: PEDRO, clientes ------------------------------------------------------------------------------------
    $c = New-Conn; $tx = $c.BeginTransaction()
    try {
        if ($deactivateTime) { Exec-Sql $c $tx 'UPDATE usuario SET updated_at = @t WHERE nombre = ''PEDRO''' @{ '@t' = $deactivateTime } }
        $k4 = 0
        foreach ($dni in $allDni) {
            $first = Get-Scalar 'SELECT MIN(created_at) FROM recibo WHERE cliente_dni = @d' @{ '@d' = $dni }
            if ($first -is [DBNull] -or $null -eq $first) { $t = $day0.AddDays(1 + ($k4 % 12)).AddHours(11).AddMinutes(7 * $k4 % 50) }
            else { $t = ([datetime]$first).AddMinutes(-3) }
            Exec-Sql $c $tx 'UPDATE cliente SET created_at = @t, updated_at = @t WHERE dni = @d' @{ '@t' = $t; '@d' = $dni }
            $k4++
        }
        $tx.Commit()
    }
    catch { $tx.Rollback(); throw }
    finally { $c.Dispose() }

    # El registro de inventario se ordena por secuencia: se reasigna la secuencia en orden cronologico (reembolsos incluidos).
    $c = New-Conn; $tx = $c.BeginTransaction()
    try {
        $invRows = @(Get-Rows 'SELECT id_movimiento, secuencia FROM movimiento_inventario ORDER BY created_at, secuencia')
        $seqValues = @($invRows | ForEach-Object { [int64]$_.secuencia } | Sort-Object)
        for ($i = 0; $i -lt $invRows.Count; $i++) { Exec-Sql $c $tx 'UPDATE movimiento_inventario SET secuencia = @n WHERE id_movimiento = @id' @{ '@n' = [int64](-1 - $i); '@id' = $invRows[$i].id_movimiento } }
        for ($i = 0; $i -lt $invRows.Count; $i++) { Exec-Sql $c $tx 'UPDATE movimiento_inventario SET secuencia = @n WHERE id_movimiento = @id' @{ '@n' = $seqValues[$i]; '@id' = $invRows[$i].id_movimiento } }
        $tx.Commit()
    }
    catch { $tx.Rollback(); throw }
    finally { $c.Dispose() }

    [EdenPOS.AppData]::SimularRechazoComprobantes = $false
    $summary['Recibos reembolsados (numeros)'] = ($recNumbers.refunded -join ', ')
    $summary['Comprobantes rechazados (recibos)'] = ($recNumbers.rejected -join ', ')
}
finally {
    try { [EdenPOS.AppSession]::SignOut() } catch { }
    if ($ownDataPath) { try { Remove-Item -Recurse -Force $DataPath -ErrorAction SilentlyContinue } catch { } }
}

# ---------------------------------------------------------------------------------------------------------------------
# Resumen
# ---------------------------------------------------------------------------------------------------------------------
Write-Host ''
Write-Host '=== Resumen de lo generado ==='
foreach ($t in 'usuario', 'cliente', 'producto', 'producto_variante', 'sesion_caja', 'movimiento_caja', 'recibo', 'recibo_linea', 'comprobante', 'movimiento_inventario', 'ticket_guardado', 'casillero_llave', 'auditoria') {
    Write-Host ("{0,-24} {1}" -f $t, (Get-Scalar "SELECT COUNT(*) FROM $t"))
}
foreach ($k in $summary.Keys) { Write-Host ("{0}: {1}" -f $k, $summary[$k]) }
Write-Host 'Listo. La facturacion simulada quedo con "Simular rechazo" desactivado.'
