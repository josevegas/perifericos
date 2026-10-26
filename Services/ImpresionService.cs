using PuntoVenta.Infrastructure.Impresion;
using PuntoVenta.Models.Impresion;
using System;
using System.Configuration;
using System.Drawing.Printing;

namespace PuntoVenta.Services
{
    /// <summary>
    /// Imprime el comprobante en la térmica de la caja.
    ///
    /// Está separado de <see cref="TicketVenta"/> a propósito: armar el papel es
    /// una función pura y esto es lo que toca el hardware. Se pueden probar por
    /// separado, y el día que haya que mandar el mismo ticket a otro lado —un
    /// PDF, un correo— se reusa el armado sin arrastrar el spooler.
    /// </summary>
    public sealed class ImpresionService
    {
        private readonly string _impresoraConfigurada;

        public ImpresionService(string impresora = null)
        {
            _impresoraConfigurada = impresora;
        }

        /// <summary>
        /// Imprime la venta recién registrada.
        ///
        /// LANZA <see cref="ImpresionException"/> si falla, y quien llame tiene
        /// que tratarlo distinto de un error de venta: a esta altura la boleta
        /// YA está registrada y numerada. No se reintenta la venta —eso
        /// duplicaría el cobro—, se reintenta el papel.
        /// </summary>
        public void Imprimir(
            ComprobanteImpresion venta, EmisorImpresion emisor, ContextoImpresion contexto,
            bool copia = false)
        {
            if (venta == null) throw new ArgumentNullException(nameof(venta));

            byte[] datos = TicketVenta.Bytes(venta, emisor, contexto, copia);

            ImpresoraRaw.Enviar(ObtenerImpresora(), datos, "Comprobante " + venta.SerieNumero);
        }

        /// <summary>
        /// Imprime la constancia de la venta anulada.
        ///
        /// Va aparte de <see cref="Imprimir"/> y no por dentro con un if: el
        /// papel, el modelo y el descargo son otros. Igual que el ticket, LANZA
        /// <see cref="ImpresionException"/> si falla, y quien llame tiene que
        /// tratarlo como un problema de papel: la anulación YA está registrada.
        /// </summary>
        public void ImprimirConstancia(
            ConstanciaAnulacionImpresion constancia, EmisorImpresion emisor,
            ContextoImpresion contexto, bool copia = false)
        {
            if (constancia == null) throw new ArgumentNullException(nameof(constancia));

            byte[] datos = TicketConstancia.Bytes(constancia, emisor, contexto, copia);

            ImpresoraRaw.Enviar(
                ObtenerImpresora(), datos, "Constancia " + constancia.Comprobante);
        }

        /// <summary>
        /// Nombres que delatan una térmica de tickets.
        ///
        /// El orden importa: primero lo más específico. "Receipt" es lo que le
        /// pone el driver de Epson a la cola.
        /// </summary>
        private static readonly string[] SenasDeTermica =
        {
            "TM-T", "TM-U", "EPSON TM", "RECEIPT", "TICKET", "THERMAL", "POS-",
        };

        /// <summary>
        /// A qué cola se manda.
        ///
        /// NUNCA cae a la impresora predeterminada. Eso fue el primer diseño y
        /// se rompió en la primera prueba: la predeterminada era una láser de
        /// red, y ESC/POS crudo salió por ahí como cinco hojas de basura. En un
        /// grifo eso es papel tirado y un cliente sin su boleta.
        ///
        /// El orden es: lo que diga la configuración; si no, una impresora que
        /// se PAREZCA a una térmica; si no, se rechaza diciendo qué hay
        /// instalado. Preferible no imprimir a imprimir en el lugar equivocado.
        ///
        /// Configurable y no quemada como en el legacy, que tiene
        /// <c>PrinterName = "EPSON"</c> repetido en seis lugares del mismo archivo.
        /// </summary>
        public static string Impresora()
        {
            string configurada =
                (ConfigurationManager.AppSettings["impresora_ticket"] ?? string.Empty).Trim();
            return ResolverImpresora(configurada);
        }

        private string ObtenerImpresora()
        {
            string configurada = !string.IsNullOrWhiteSpace(_impresoraConfigurada)
                ? _impresoraConfigurada.Trim()
                : (ConfigurationManager.AppSettings["impresora_ticket"] ?? string.Empty).Trim();
            return ResolverImpresora(configurada);
        }

        private static string ResolverImpresora(string configurada)
        {
            if (!string.IsNullOrWhiteSpace(configurada)) return configurada;

            string adivinada = BuscarTermica();
            if (adivinada != null) return adivinada;

            throw new ImpresionException(
                "No hay ninguna impresora térmica reconocible en esta caja.\n\n" +
                "Instaladas: " + string.Join(", ", Instaladas()) + "\n\n" +
                "Pon el nombre exacto en 'impresora_ticket' del App.config.", 0);
        }

        /// <summary>La primera cola cuyo nombre delata una térmica.</summary>
        private static string BuscarTermica()
        {
            foreach (string sena in SenasDeTermica)
            {
                foreach (string nombre in Instaladas())
                {
                    if (nombre.ToUpperInvariant().Contains(sena))
                        return nombre;
                }
            }
            return null;
        }

        private static string[] Instaladas()
        {
            var nombres = new System.Collections.Generic.List<string>();
            foreach (string nombre in PrinterSettings.InstalledPrinters)
                nombres.Add(nombre);
            return nombres.ToArray();
        }

        /// <summary>
        /// Abre la gaveta sin imprimir nada.
        ///
        /// Va aparte del ticket porque el cajero a veces necesita el cajón sin
        /// una venta de por medio: dar cambio, guardar un depósito.
        /// </summary>
        public void AbrirGaveta()
        {
            ImpresoraRaw.Enviar(
                ObtenerImpresora(),
                EscPos.Codificacion.GetBytes(EscPos.AbrirGaveta),
                "Abrir gaveta");
        }
    }
}
