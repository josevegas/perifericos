namespace PuntoVenta.Models.Impresion
{
    /// <summary>
    /// Constancia de anulación: el papel que acredita que un comprobante ya
    /// emitido quedó sin efecto y que su importe se devolvió al cliente.
    ///
    /// NO ES UN COMPROBANTE TRIBUTARIO. No se declara ante SUNAT, así que no
    /// lleva QR ni resolución de autorización impresa. El hash que sí trae es
    /// el del comprobante ANULADO —el que el cliente ya tiene en la mano—, y
    /// se imprime rotulado como tal para que nadie lo confunda con un hash
    /// propio de este papel.
    /// </summary>
    public sealed class ConstanciaAnulacionImpresion
    {
        /// <summary>Número del comprobante anulado, p. ej. <c>B019-00800145</c>.</summary>
        public string Comprobante { get; set; }

        /// <summary>"BOLETA DE VENTA" o "FACTURA", como lo nombra el comprobante.</summary>
        public string TipoComprobante { get; set; }

        public string FechaEmision { get; set; }
        public string HoraEmision { get; set; }

        /// <summary>Documento del cliente del comprobante anulado (DNI o RUC).</summary>
        public string ClienteDocumento { get; set; }

        public decimal ImporteTotal { get; set; }

        public string FechaAnulacion { get; set; }
        public string HoraAnulacion { get; set; }

        /// <summary>Por qué se anuló. Es lo que hace que este papel acredite algo.</summary>
        public string Motivo { get; set; }

        public decimal MontoDevuelto { get; set; }

        /// <summary>EFECTIVO, TARJETA o BILLETERA: cómo volvió el dinero.</summary>
        public string MedioDevuelto { get; set; }

        /// <summary>
        /// Queda pendiente la comunicación de baja ante SUNAT. <c>null</c> cuando
        /// la venta no tuvo comprobante electrónico: no hay nada que dar de baja.
        /// </summary>
        public bool? BajaPendiente { get; set; }

        /// <summary>Estado del comprobante ante SUNAT, si se llegó a emitir.</summary>
        public string EstadoComprobante { get; set; }

        /// <summary>Hash (digest) del comprobante anulado, no de esta constancia.</summary>
        public string Hash { get; set; }

        /// <summary>Sin número de comprobante no hay nada que constatar.</summary>
        public bool PuedeImprimir()
        {
            return !string.IsNullOrWhiteSpace(Comprobante);
        }
    }
}
