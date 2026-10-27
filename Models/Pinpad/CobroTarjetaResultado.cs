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

        /// <summary>
        /// Marca/producto con el que se cobró, tal como lo reporta el equipo
        /// (tag DF FF 41: "VISA DEBITO", …). Es la única pista del medio usado:
        /// el protocolo no lo entrega como campo propio.
        /// </summary>
        public string MarcaEquipo { get; set; }

        /// <summary>Tipo de cuenta que usó el cliente (tag DF FF 40: "DEBITO", "CREDITO").</summary>
        public string TipoCuenta { get; set; }

        /// <summary>Producto/marca detallada del medio (tag DF FF 44: "VISA INTERNACIONAL", …).</summary>
        public string ProductoEquipo { get; set; }

        /// <summary>
        /// El cobro se hizo con una billetera (Yape, Plin…) en vez de una tarjeta.
        /// Se deduce del texto del equipo — ver <c>PinpadService.EsBilletera</c>.
        /// </summary>
        public bool Billetera { get; set; }
    }
}
