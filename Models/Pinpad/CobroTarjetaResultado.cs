namespace PuntoVenta.Models.Pinpad
{
    /// <summary>
    /// Resultado de un cobro por el pinpad Niubiz (Ingenico/PCL).
    /// Los campos estructurados salen del VOUCHER que devuelve el equipo
    /// (el protocolo no los entrega por separado — ver PinpadService.ParsearVoucher).
    /// Son los mismos que el legacy persistía en Ventapref y soportan la anulación.
    /// </summary>
    public sealed class CobroTarjetaResultado
    {
        public bool Aprobado { get; set; }

        /// <summary>Código de autorización (AP del voucher).</summary>
        public string Autorizacion { get; set; }

        /// <summary>Número de operación (REF del voucher).</summary>
        public string Referencia { get; set; }

        /// <summary>Lote del terminal (LOTE del voucher).</summary>
        public string Lote { get; set; }

        /// <summary>Terminal físico de Niubiz (TER del voucher).</summary>
        public string TerminalPos { get; set; }

        /// <summary>Modo de captura de la tarjeta (CAPTURA del voucher).</summary>
        public string Captura { get; set; }

        /// <summary>Voucher del comercio, texto imprimible tal como llegó.</summary>
        public string VoucherComercio { get; set; }

        /// <summary>Voucher del cliente, texto imprimible tal como llegó.</summary>
        public string VoucherCliente { get; set; }

        /// <summary>Respuesta TLV cruda (hex) — solo para diagnóstico.</summary>
        public string RespuestaCruda { get; set; }

        public string MensajeError { get; set; }

        /// <summary>Código de respuesta del equipo/banco: "00" aprobado, "05" denegada…</summary>
        public string CodigoRespuesta { get; set; }

        /// <summary>Texto que devolvió el equipo (ej. "DENEGADA", "TRANSACCION EXITOSA").</summary>
        public string MensajeEquipo { get; set; }
    }
}
