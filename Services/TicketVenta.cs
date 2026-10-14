using PuntoVenta.Infrastructure.Impresion;
using PuntoVenta.Models.Impresion;
using System;
using System.Globalization;
using System.Text;

namespace PuntoVenta.Services
{
    /// <summary>
    /// Datos operativos del ticket que no salen ni de la venta ni del emisor.
    ///
    /// Objeto aparte y no tres parámetros sueltos en <see cref="TicketVenta.Armar"/>:
    /// cajero y turno son de la SESIÓN, <see cref="NroSerieMaquina"/> es de la
    /// TERMINAL — mezclarlos en la firma del método escondería de dónde sale
    /// cada uno.
    /// </summary>
    public sealed class ContextoImpresion
    {
        public string Cajero { get; }
        public int? TurnoNumero { get; }

        /// <summary>"M. REG." del comprobante impreso — ver <c>ConfiguracionTerminal.NroSerieMaquina</c>.</summary>
        public string NroSerieMaquina { get; }

        public ContextoImpresion(string cajero, int? turnoNumero, string nroSerieMaquina)
        {
            Cajero = cajero;
            TurnoNumero = turnoNumero;
            NroSerieMaquina = nroSerieMaquina;
        }
    }

    /// <summary>
    /// Arma el comprobante para la impresora térmica.
    ///
    /// Es una función pura: entra la venta, el emisor y el contexto, sale el
    /// texto con sus comandos ESC/POS. No toca la impresora ni la red — así se
    /// puede mirar el ticket en pantalla, o compararlo en un test, sin gastar
    /// papel.
    ///
    /// SE ARMA ACÁ Y NO EN EL BACKEND porque imprimir tiene que funcionar
    /// aunque la red se caiga justo después de registrar: a esa altura la
    /// boleta ya existe y el cliente está esperando su papel.
    ///
    /// Ancho: 42 columnas, medido en la TM-T20III con papel de 80 mm.
    ///
    /// LO QUE EL TICKET NO TIENE, A PROPÓSITO: el código QR, el texto sobre el
    /// QR, "AUTORIZADO MEDIANTE RESOLUCIÓN" y la URL de validación. Esos cuatro
    /// no son formato — son la constancia de que SUNAT aceptó el comprobante,
    /// y solo existen si de verdad se emitió por Bizlinks (el legacy los
    /// espera con <c>frmMenu.esperaRespuestaBizlinks</c> antes de imprimir).
    /// Imprimirlos sin haber emitido sería falsificar un documento tributario.
    /// </summary>
    public static class TicketVenta
    {
        private static readonly CultureInfo Peru = new CultureInfo("es-PE");

        /// <summary>
        /// El ticket completo, listo para <see cref="ImpresoraRaw"/>.
        /// </summary>
        /// <param name="copia">
        /// Marca el papel como reimpresión. Un duplicado sin marcar puede
        /// pasar por un segundo cobro en el arqueo.
        /// </param>
        public static string Armar(
            ComprobanteImpresion venta, EmisorImpresion emisor, ContextoImpresion contexto,
            bool copia = false)
        {
            if (venta == null) throw new ArgumentNullException(nameof(venta));
            if (contexto == null) throw new ArgumentNullException(nameof(contexto));
            if (emisor == null)
                throw new ImpresionException(
                    "Esta estación no tiene cargados los datos del emisor. " +
                    "Sin RUC ni razón social el comprobante no es válido.", 0);
            if (!emisor.PuedeEmitir())
                throw new ImpresionException(
                    "Faltan el RUC o la razón social de la estación. " +
                    "Cárgalos antes de imprimir comprobantes.", 0);

            var t = new StringBuilder();
            t.Append(EscPos.Inicio);

            Cabecera(t, emisor);
            Documento(t, venta, contexto, copia);
            Detalle(t, venta);
            Totales(t, venta);
            FormaDePago(t, venta);
            Pagos(t, venta);
            MontoEnLetras(t, venta);
            DatosDelDespacho(t, venta);
            Pie(t, contexto, copia);

            t.Append(EscPos.Cortar);
            return t.ToString();
        }

        /// <summary>Los bytes tal como los recibe la impresora.</summary>
        public static byte[] Bytes(
            ComprobanteImpresion venta, EmisorImpresion emisor, ContextoImpresion contexto,
            bool copia = false)
        {
            return EscPos.Codificacion.GetBytes(Armar(venta, emisor, contexto, copia));
        }

        // --- Bloques -----------------------------------------------------------

        private static void Cabecera(StringBuilder t, EmisorImpresion emisor)
        {
            t.Append(EscPos.Centro);

            // El nombre comercial es el que reconoce el cliente; la razón social
            // es la que exige SUNAT. Van los dos, con el comercial destacado.
            string titulo = string.IsNullOrWhiteSpace(emisor.NombreComercial)
                ? emisor.RazonSocial
                : emisor.NombreComercial;

            // Doble ancho: cada carácter ocupa 2, así que el límite es la
            // mitad de las columnas normales.
            t.Append(EscPos.Grande).Append(Recortar(titulo, EscPos.Columnas / 2)).Append('\n')
             .Append(EscPos.Normal);

            if (!string.IsNullOrWhiteSpace(emisor.NombreComercial))
                Lineas(t, emisor.RazonSocial);

            // Dirección FÍSICA de esta sede primero, después la de ubicación,
            // y recién el domicilio LEGAL ligado al RUC (el mismo para las 21
            // estaciones). SUNAT exige mostrar las dos y no son la misma cosa.
            Lineas(t, emisor.Direccion);
            string ubicacion = emisor.Ubicacion();
            if (!string.IsNullOrWhiteSpace(ubicacion)) Lineas(t, ubicacion);
            if (!string.IsNullOrWhiteSpace(emisor.DomicilioFiscal))
                Lineas(t, emisor.DomicilioFiscal);

            t.Append("RUC ").Append(emisor.Ruc.Trim()).Append('\n');

            if (!string.IsNullOrWhiteSpace(emisor.Telefono))
                t.Append("Telf. ").Append(emisor.Telefono.Trim()).Append('\n');
        }

        private static void Documento(
            StringBuilder t, ComprobanteImpresion venta, ContextoImpresion contexto, bool copia)
        {
            t.Append('\n').Append(EscPos.Centro).Append(EscPos.NegritaOn);

            if (copia) t.Append("** COPIA **\n");
            t.Append(Titulo(venta.TipoDocumento)).Append(" ELECTRONICA\n")
             .Append(venta.SerieNumero).Append('\n')
             .Append(EscPos.NegritaOff);

            t.Append(EscPos.Izquierda).Append('\n');

            // Fecha y hora comparten renglón, como en el comprobante real: la
            // hora sola ocuparía una línea entera de un papel que se paga por
            // centímetro.
            string fecha = "Fecha: " + FechaLegible(venta.FechaEmision);
            t.Append(string.IsNullOrWhiteSpace(venta.HoraEmision)
                ? fecha
                : EscPos.Fila(fecha, "Hora: " + venta.HoraEmision.Trim())).Append('\n');

            if (!string.IsNullOrWhiteSpace(contexto.NroSerieMaquina))
                t.Append("M. Reg.: ").Append(contexto.NroSerieMaquina.Trim()).Append('\n');

            // El rótulo va SIEMPRE, aunque el dato esté vacío: es una boleta sin
            // cliente identificado, no una boleta a la que se le olvidó el campo.
            // Es lo que hace el comprobante que ya emiten hoy.
            t.Append("Cliente: ").Append(Cliente(venta)).Append('\n');
        }

        /// <summary>Razón social del cliente, con su documento si lo hay.</summary>
        private static string Cliente(ComprobanteImpresion venta)
        {
            string razon = (venta.ClienteRazon ?? string.Empty).Trim();
            string documento = (venta.ClienteDocumento ?? string.Empty).Trim();

            if (razon.Length == 0 && documento.Length == 0) return string.Empty;
            if (documento.Length == 0) return Recortar(razon, EscPos.Columnas - 9);
            if (razon.Length == 0) return documento;
            return Recortar(razon + " - " + documento, EscPos.Columnas - 9);
        }

        // Ancho de cada columna. 24+1+9+1+5+1+8+1+14 = 64, justo EscPos.Columnas.
        // Con la fuente chica sobra lugar: el nombre mas largo del catalogo
        // ("GASOHOL PREMIUM") son 15 y entra holgado en 24.
        //
        // El importe va SIN el prefijo "S/": la cabecera de la columna ya dice
        // IMPORTE, igual que en el ticket real, y ademas con separador de miles
        // "S/ 3,463.20" son 11 caracteres contra los 8 del numero solo.
        private const int AnchoProducto = 24;
        private const int AnchoCantidad = 9;   // "130.000" del diesel, con holgura
        private const int AnchoUnidad = 5;     // "U.MED" del encabezado son 5
        private const int AnchoPrecio = 8;
        private const int AnchoImporte = 14;   // "3,463.20" y lo que venga

        private static void Detalle(StringBuilder t, ComprobanteImpresion venta)
        {
            t.Append(EscPos.Separador()).Append('\n');
            t.Append(FilaProducto("PRODUCTO", "CANT.", "U.MED", "PRECIO", "IMPORTE")).Append('\n');
            t.Append(EscPos.Separador()).Append('\n');

            if (venta.Linea == null) return;

            {
                LineaComprobanteImpresion linea = venta.Linea;
                t.Append(FilaProducto(
                    linea.Descripcion,
                    Numero(linea.Galones, 3),
                    "GLL",              // todo lo que se despacha acá va en galones
                    Numero(linea.Precio, 2),
                    Numero(linea.Total, 2))).Append('\n');
            }
        }

        /// <summary>Una fila de la tabla del detalle, en sus 5 columnas fijas.</summary>
        private static string FilaProducto(
            string producto, string cantidad, string unidad, string precio, string importe)
        {
            return PadDerecha(producto, AnchoProducto) + " " +
                   PadIzquierda(cantidad, AnchoCantidad) + " " +
                   PadDerecha(unidad, AnchoUnidad) + " " +
                   PadIzquierda(precio, AnchoPrecio) + " " +
                   PadIzquierda(importe, AnchoImporte);
        }

        private static void Totales(StringBuilder t, ComprobanteImpresion venta)
        {
            t.Append(EscPos.Separador()).Append('\n');
            t.Append(EscPos.Fila("OP. GRAVADA", Monto(venta.Subtotal))).Append('\n');
            t.Append(EscPos.Fila("IGV (18%)", Monto(venta.Igv))).Append('\n');
            t.Append(EscPos.NegritaOn)
             .Append(EscPos.Fila("TOTAL", Monto(venta.Total))).Append('\n')
             .Append(EscPos.NegritaOff);
        }

        /// <summary>"Forma pago: CONTADO". La CONDICIÓN comercial de la venta,
        /// no el instrumento con el que se cobró —eso va en <see cref="Pagos"/>—.</summary>
        private static void FormaDePago(StringBuilder t, ComprobanteImpresion venta)
        {
            if (string.IsNullOrWhiteSpace(venta.CondicionPago)) return;
            t.Append(EscPos.Fila("Forma pago", venta.CondicionPago.Trim())).Append('\n');
        }

        private static void Pagos(StringBuilder t, ComprobanteImpresion venta)
        {
            if (venta.Pagos == null || venta.Pagos.Count == 0) return;

            t.Append('\n');
            foreach (PagoComprobanteImpresion pago in venta.Pagos)
            {
                t.Append(EscPos.Fila(
                    Recortar(pago.Descripcion, 22), Monto(pago.Monto))).Append('\n');

                // El número de operación es lo que permite reclamar al banco: si
                // no sale impreso, el cliente no tiene con qué.
                if (!string.IsNullOrWhiteSpace(pago.NumeroOperacion))
                    t.Append("  Op. ").Append(pago.NumeroOperacion.Trim()).Append('\n');
            }

            if (venta.Vuelto > 0m)
                t.Append(EscPos.Fila("VUELTO", Monto(venta.Vuelto))).Append('\n');
        }

        /// <summary>
        /// "SON: VEINTE SOLES", el monto TOTAL en letras.
        ///
        /// Va ENVUELTO y no en una línea suelta: un monto de miles se pasa del
        /// ancho del papel ("SON: TRES MIL CUATROCIENTOS SESENTA Y OCHO CON
        /// 20/100 SOLES" son 58 caracteres contra 48 de ancho), y la impresora
        /// lo cortaría donde se le acabe el renglón.
        /// </summary>
        private static void MontoEnLetras(StringBuilder t, ComprobanteImpresion venta)
        {
            Lineas(t, NumeroEnLetras.Son(venta.Total));
        }

        /// <summary>
        /// Lado, manguera y placa, como RESUMEN — no por línea.
        ///
        /// Se imprime solo cuando TODAS las líneas comparten un único lado: es
        /// el caso normal de un despacho por surtidor ("una boleta, un solo
        /// lado"). Con varios lados distintos, o ninguno —una venta manual sin
        /// despacho—, no hay nada limpio que resumir y se omite: inventar un
        /// valor sería peor que no mostrarlo.
        ///
        /// La manguera se convierte a letra (1=a, 2=b, ...) siguiendo la
        /// convención del ticket legacy real; si algún día se confirma que no
        /// es así, es un cambio de una línea.
        /// </summary>
        private static void DatosDelDespacho(StringBuilder t, ComprobanteImpresion venta)
        {
            LineaComprobanteImpresion linea = venta.Linea;
            if (linea == null || linea.Lado == null) return;

            t.Append(EscPos.Fila("Lado", linea.Lado.Value.ToString("D2", Peru))).Append('\n');

            // Manguera y placa se imprimen si vinieron. Una venta registra UN
            // despacho, asi que salen directo de esa linea.
            if (linea.Manguera != null)
                t.Append(EscPos.Fila("Manguera", LetraDeManguera(linea.Manguera.Value))).Append('\n');

            string placa = (linea.Placa ?? string.Empty).Trim();
            if (placa.Length > 0)
                t.Append(EscPos.Fila("Placa", placa)).Append('\n');
        }

        private static string LetraDeManguera(int numero)
        {
            if (numero < 1 || numero > 26) return numero.ToString(Peru);
            return ((char)('a' + numero - 1)).ToString();
        }

        private static void Pie(StringBuilder t, ContextoImpresion contexto, bool copia)
        {
            t.Append('\n');
            if (!string.IsNullOrWhiteSpace(contexto.Cajero))
                t.Append("Cajero: ").Append(Recortar(contexto.Cajero, 32)).Append('\n');
            if (contexto.TurnoNumero != null)
                t.Append("Turno: ").Append(contexto.TurnoNumero.Value.ToString(Peru)).Append('\n');

            // La hora de IMPRESIÓN, no la de emisión: en una reimpresión son
            // distintas, y saber cuándo salió cada papel resuelve discusiones.
            // Con la VERSIÓN al lado, porque una caja con el ejecutable viejo
            // imprime un ticket que parece correcto pero le faltan campos, y
            // sin esto no hay forma de saberlo mirando el papel.
            t.Append("Impreso: ")
             .Append(DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss", Peru))
             .Append("  v").Append(Version()).Append('\n');

            t.Append('\n').Append(EscPos.Centro);
            if (copia)
                t.Append("REIMPRESION - no es un segundo cobro\n");
            t.Append("Gracias por su compra\n");
            t.Append('\n').Append(EscPos.Izquierda);
        }

        // --- Formato -----------------------------------------------------------

        /// <summary>
        /// Cuándo se compiló el ejecutable, como <c>MMdd.HHmm</c>.
        ///
        /// Va impreso en el pie para poder detectar de un vistazo que una caja
        /// quedó con una versión vieja: el ticket sale "bien" pero sin los
        /// campos nuevos, y sin esto hay que adivinar. Se usa la fecha de
        /// compilación y no un número de versión porque no hay que acordarse de
        /// subirlo en cada cambio.
        /// </summary>
        private static string Version()
        {
            try
            {
                string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                return System.IO.File.GetLastWriteTime(exe).ToString("MMdd.HHmm", Peru);
            }
            catch (Exception)
            {
                return "?";
            }
        }

        private static string Titulo(string tipo)
        {
            return string.Equals(tipo, "FACTURA", StringComparison.OrdinalIgnoreCase)
                ? "FACTURA"
                : "BOLETA DE VENTA";
        }

        /// <summary>"S/ 5.00", siempre con dos decimales.</summary>
        private static string Monto(decimal valor)
        {
            return "S/ " + valor.ToString("N2", Peru);
        }

        private static string Numero(decimal valor, int decimales)
        {
            return valor.ToString("N" + decimales, Peru);
        }

        /// <summary>
        /// La fecha llega ISO del backend. Se muestra dd/MM/yyyy, que es como se
        /// lee acá; si viniera en otro formato se imprime tal cual antes que
        /// dejar el renglón vacío.
        /// </summary>
        private static string FechaLegible(string iso)
        {
            DateTime fecha;
            if (DateTime.TryParse(iso, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out fecha))
            {
                return fecha.ToString("dd/MM/yyyy", Peru);
            }
            return iso ?? string.Empty;
        }

        private static string Recortar(string texto, int largo)
        {
            texto = (texto ?? string.Empty).Trim();
            return texto.Length <= largo ? texto : texto.Substring(0, largo);
        }

        /// <summary>Recorta a <paramref name="ancho"/> y rellena con espacios a
        /// la derecha. El texto va al margen izquierdo de su columna.</summary>
        private static string PadDerecha(string texto, int ancho)
        {
            return Recortar(texto, ancho).PadRight(ancho);
        }

        /// <summary>Recorta a <paramref name="ancho"/> y rellena con espacios a
        /// la izquierda. Para números: quedan alineados a la derecha de su
        /// columna, como se leen las cifras.</summary>
        private static string PadIzquierda(string texto, int ancho)
        {
            return Recortar(texto, ancho).PadLeft(ancho);
        }

        /// <summary>Vuelca un texto largo partido en líneas que entran a lo ancho.</summary>
        private static void Lineas(StringBuilder t, string texto)
        {
            foreach (string linea in EscPos.Envolver(texto))
                t.Append(linea).Append('\n');
        }
    }
}
