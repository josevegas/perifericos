using PuntoVenta.Infrastructure.Impresion;
using PuntoVenta.Models.Impresion;
using System;
using System.Text;

namespace PuntoVenta.Services
{
    /// <summary>
    /// Arma la constancia de anulación para la térmica.
    ///
    /// Es el papel que se le entrega al cliente cuando se le devuelve el
    /// importe de un comprobante ya emitido: dice qué documento quedó sin
    /// efecto, por qué y cuánto se devolvió. Sin él, el cliente sale de la caja
    /// con su boleta original en la mano y sin nada que explique la devolución.
    ///
    /// NO ES UN COMPROBANTE TRIBUTARIO: no se declara ante SUNAT y por eso no
    /// lleva QR ni resolución de autorización. El hash que imprime es el del
    /// comprobante anulado, rotulado como tal, para poder cotejarlo contra el
    /// papel que el cliente ya tenía.
    ///
    /// Como <see cref="TicketVenta"/>, es una función pura: entra el modelo y
    /// sale el texto con sus comandos ESC/POS, sin tocar la impresora ni la
    /// red. Comparte con el ticket la cabecera del emisor y el pie —cajero,
    /// turno, caja y hora de impresión— porque el cliente tiene que reconocer
    /// el mismo negocio en los dos papeles.
    /// </summary>
    public static class TicketConstancia
    {
        /// <summary>El papel completo, listo para <see cref="ImpresoraRaw"/>.</summary>
        /// <param name="copia">
        /// Marca el papel como duplicado. El original se queda en caja como
        /// respaldo del egreso: sin esa marca, dos papeles iguales pueden
        /// registrarse como dos devoluciones en el arqueo.
        /// </param>
        public static string Armar(
            ConstanciaAnulacionImpresion constancia, EmisorImpresion emisor,
            ContextoImpresion contexto, bool copia = false)
        {
            if (constancia == null) throw new ArgumentNullException(nameof(constancia));
            if (contexto == null) throw new ArgumentNullException(nameof(contexto));
            if (emisor == null)
                throw new ImpresionException(
                    "Esta estación no tiene cargados los datos del emisor. " +
                    "Sin RUC ni razón social la constancia no acredita nada.", 0);
            if (!emisor.PuedeEmitir())
                throw new ImpresionException(
                    "Faltan el RUC o la razón social de la estación. " +
                    "Cárgalos antes de imprimir la constancia.", 0);
            if (!constancia.PuedeImprimir())
                throw new ImpresionException(
                    "La constancia no dice qué comprobante se anuló.", 0);

            var t = new StringBuilder();
            t.Append(EscPos.Inicio);

            TicketVenta.Cabecera(t, emisor);
            Encabezado(t, constancia, contexto, copia);
            ComprobanteAnulado(t, constancia);
            Devolucion(t, constancia);
            EstadoAnteSunat(t, constancia);
            Firma(t);
            TicketVenta.Pie(t, contexto);
            Despedida(t, copia);

            t.Append(EscPos.Cortar);
            return t.ToString();
        }

        /// <summary>Los bytes tal como los recibe la impresora.</summary>
        public static byte[] Bytes(
            ConstanciaAnulacionImpresion constancia, EmisorImpresion emisor,
            ContextoImpresion contexto, bool copia = false)
        {
            return EscPos.Codificacion.GetBytes(Armar(constancia, emisor, contexto, copia));
        }

        // --- Bloques -----------------------------------------------------------

        private static void Encabezado(
            StringBuilder t, ConstanciaAnulacionImpresion constancia,
            ContextoImpresion contexto, bool copia)
        {
            t.Append('\n').Append(EscPos.Centro).Append(EscPos.NegritaOn);

            // La marca de copia va arriba y aparte, donde no se confunde con el
            // título: es lo primero que se mira para saber si el papel es el
            // original que se quedó en caja o el duplicado.
            if (copia) t.Append("** COPIA **\n");

            t.Append("CONSTANCIA DE ANULACION\n").Append(EscPos.NegritaOff);

            t.Append(EscPos.Izquierda).Append('\n');

            // Fecha y hora de la ANULACIÓN, en un solo renglón: el papel se paga
            // por centímetro y la hora sola gastaría una línea entera. El rótulo
            // dice "Anulado" y no "Fecha" a propósito: más abajo se imprime la
            // fecha de emisión del comprobante y dos "Fecha" seguidas se leen
            // como la misma.
            t.Append(EscPos.Fila(
                "Anulado", FechaYHora(constancia.FechaAnulacion, constancia.HoraAnulacion)))
             .Append('\n');

            if (!string.IsNullOrWhiteSpace(contexto.NroSerieMaquina))
                t.Append("M. Reg.: ").Append(contexto.NroSerieMaquina.Trim()).Append('\n');
        }

        /// <summary>
        /// Qué documento quedó sin efecto.
        ///
        /// El número se imprime a la vista y con los ceros a la izquierda,
        /// porque es el dato que el cliente coteja contra el papel que tiene en
        /// la mano.
        /// </summary>
        private static void ComprobanteAnulado(
            StringBuilder t, ConstanciaAnulacionImpresion constancia)
        {
            t.Append(EscPos.Separador()).Append('\n');
            t.Append(EscPos.Centro).Append(EscPos.NegritaOn)
             .Append("COMPROBANTE ANULADO\n")
             .Append(TicketVenta.Recortar(constancia.TipoComprobante, EscPos.Columnas)).Append('\n')
             .Append(TicketVenta.Recortar(constancia.Comprobante, EscPos.Columnas)).Append('\n')
             .Append(EscPos.NegritaOff).Append(EscPos.Izquierda);

            t.Append(EscPos.Fila(
                "Emitido", FechaYHora(constancia.FechaEmision, constancia.HoraEmision))).Append('\n');

            string cliente = (constancia.ClienteDocumento ?? string.Empty).Trim();
            if (cliente.Length > 0)
                t.Append(EscPos.Fila("Cliente", cliente)).Append('\n');

            t.Append(EscPos.Fila("Importe", TicketVenta.Monto(constancia.ImporteTotal))).Append('\n');
        }

        /// <summary>Cuánto volvió, por qué medio y por qué se anuló.</summary>
        private static void Devolucion(StringBuilder t, ConstanciaAnulacionImpresion constancia)
        {
            t.Append('\n').Append(EscPos.Separador()).Append('\n');
            t.Append(EscPos.Centro).Append(EscPos.NegritaOn)
             .Append("DEVOLUCION\n").Append(EscPos.NegritaOff).Append(EscPos.Izquierda);

            // El motivo se imprime COMPLETO y envuelto: es la razón de ser de
            // este papel. Recortarlo lo dejaría sin contenido.
            TicketVenta.Lineas(t, "Motivo: " + (constancia.Motivo ?? string.Empty).Trim());

            t.Append(EscPos.NegritaOn)
             .Append(EscPos.Fila("Devuelto", TicketVenta.Monto(constancia.MontoDevuelto))).Append('\n')
             .Append(EscPos.NegritaOff);

            string medio = (constancia.MedioDevuelto ?? string.Empty).Trim();
            if (medio.Length > 0)
                t.Append(EscPos.Fila("Medio", medio)).Append('\n');

            // El importe en letras va DEBAJO del número y envuelto: es lo que
            // vale cuando el número no se entiende, igual que en el ticket.
            TicketVenta.Lineas(t, NumeroEnLetras.Son(constancia.MontoDevuelto));
        }

        /// <summary>
        /// El rastro electrónico del comprobante anulado.
        ///
        /// Si la venta no se emitió por Bizlinks no hay estado ni hash, y no se
        /// imprime nada: un renglón vacío sugeriría un trámite que no existe.
        /// </summary>
        private static void EstadoAnteSunat(
            StringBuilder t, ConstanciaAnulacionImpresion constancia)
        {
            string estado = (constancia.EstadoComprobante ?? string.Empty).Trim();
            string hash = (constancia.Hash ?? string.Empty).Trim();

            if (estado.Length == 0 && hash.Length == 0 && constancia.BajaPendiente == null)
                return;

            t.Append('\n');
            if (estado.Length > 0)
                t.Append(EscPos.Fila("Estado SUNAT", estado)).Append('\n');
            if (constancia.BajaPendiente != null)
                t.Append(EscPos.Fila(
                    "Baja pendiente", constancia.BajaPendiente.Value ? "SI" : "NO")).Append('\n');
            if (hash.Length > 0)
            {
                t.Append("Hash del comprobante:\n");
                TicketVenta.Lineas(t, hash);
            }
        }

        /// <summary>
        /// Renglón para la firma de quien recibe la devolución.
        ///
        /// El original se archiva en caja: sin firma, el egreso de efectivo no
        /// tiene con qué sostenerse en el arqueo del turno.
        /// </summary>
        private static void Firma(StringBuilder t)
        {
            t.Append('\n').Append(EscPos.Separador()).Append('\n')
             .Append("Recibi conforme:\n\n\n")
             .Append(EscPos.Separador()).Append('\n');
        }

        private static void Despedida(StringBuilder t, bool copia)
        {
            t.Append('\n').Append(EscPos.Centro);
            if (copia) t.Append("COPIA - el original quedo en caja\n");
            t.Append("Conserve este documento\n");
            t.Append('\n').Append(EscPos.Izquierda);
        }

        // --- Formato -----------------------------------------------------------

        private static string FechaYHora(string fecha, string hora)
        {
            string legible = TicketVenta.FechaLegible(fecha);
            string horaTexto = (hora ?? string.Empty).Trim();
            return horaTexto.Length == 0 ? legible : legible + " " + horaTexto;
        }
    }
}
