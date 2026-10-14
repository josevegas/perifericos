using System;
using System.Runtime.InteropServices;

namespace PuntoVenta.Infrastructure.Impresion
{
    /// <summary>
    /// Manda bytes CRUDOS a una impresora, a través del spooler de Windows.
    ///
    /// El POS legacy no hace esto: dibuja el ticket con <c>Graphics.DrawString</c>
    /// y lo manda al driver, que termina imprimiéndolo como una IMAGEN. Por eso
    /// tarda segundos en una térmica que puede escupir el papel al instante.
    ///
    /// Acá se abre la cola con el tipo de datos <c>RAW</c>, que le dice a
    /// Windows que no interprete nada: los bytes llegan tal cual a la impresora,
    /// que es lo que ESC/POS necesita.
    ///
    /// POR QUÉ POR EL SPOOLER Y NO AL PUERTO
    /// -------------------------------------
    /// Escribir directo a USB o a COM ataría la app a cómo está conectada CADA
    /// caja. Por el spooler da igual —USB, serie o red—: Windows enruta. La
    /// misma app sirve para la caja de acá y para una futura por Ethernet.
    /// </summary>
    public static class ImpresoraRaw
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DOCINFO
        {
            [MarshalAs(UnmanagedType.LPWStr)] public string pDocName;
            [MarshalAs(UnmanagedType.LPWStr)] public string pOutputFile;
            [MarshalAs(UnmanagedType.LPWStr)] public string pDataType;
        }

        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool OpenPrinter(string src, out IntPtr hPrinter, IntPtr pd);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool ClosePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool StartDocPrinter(IntPtr hPrinter, int level, ref DOCINFO di);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool EndDocPrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool StartPagePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool EndPagePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool WritePrinter(
            IntPtr hPrinter, IntPtr pBytes, int count, out int written);

        /// <summary>
        /// Envía los bytes a esa cola. <paramref name="titulo"/> es lo que se ve
        /// en la ventana de impresión de Windows si algo se atasca.
        /// </summary>
        public static void Enviar(string impresora, byte[] datos, string titulo)
        {
            if (string.IsNullOrWhiteSpace(impresora))
                throw new ArgumentException("Falta el nombre de la impresora.", nameof(impresora));
            if (datos == null || datos.Length == 0)
                throw new ArgumentException("No hay nada que imprimir.", nameof(datos));

            IntPtr cola;
            if (!OpenPrinter(impresora, out cola, IntPtr.Zero))
                throw new ImpresionException(
                    "No se pudo abrir la impresora '" + impresora + "'. " +
                    "Revisa que esté encendida y con ese nombre en Windows.",
                    Marshal.GetLastWin32Error());

            try
            {
                var doc = new DOCINFO
                {
                    pDocName = titulo,
                    pDataType = "RAW",   // sin esto el driver reinterpreta los bytes
                };

                if (!StartDocPrinter(cola, 1, ref doc))
                    throw new ImpresionException(
                        "La impresora no aceptó el documento.", Marshal.GetLastWin32Error());
                try
                {
                    if (!StartPagePrinter(cola))
                        throw new ImpresionException(
                            "La impresora no aceptó la página.", Marshal.GetLastWin32Error());

                    IntPtr buffer = Marshal.AllocCoTaskMem(datos.Length);
                    try
                    {
                        Marshal.Copy(datos, 0, buffer, datos.Length);
                        int escritos;
                        if (!WritePrinter(cola, buffer, datos.Length, out escritos))
                            throw new ImpresionException(
                                "Falló el envío a la impresora.", Marshal.GetLastWin32Error());

                        // Un envío parcial deja un ticket cortado a la mitad, que
                        // es peor que ninguno: el cliente se lleva medio papel.
                        if (escritos != datos.Length)
                            throw new ImpresionException(
                                "El ticket se envió incompleto (" + escritos + " de " +
                                datos.Length + " bytes).", 0);
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(buffer);
                    }
                    EndPagePrinter(cola);
                }
                finally
                {
                    EndDocPrinter(cola);
                }
            }
            finally
            {
                ClosePrinter(cola);
            }
        }
    }

    /// <summary>
    /// Falló la impresión. Se distingue del resto de errores porque tiene una
    /// consecuencia propia: la venta YA está registrada y lo único que falta es
    /// el papel, así que no hay que reintentar la venta, hay que reimprimir.
    /// </summary>
    public sealed class ImpresionException : Exception
    {
        public ImpresionException(string mensaje, int codigoWindows)
            : base(codigoWindows == 0
                ? mensaje
                : mensaje + " (error de Windows " + codigoWindows + ")")
        {
            CodigoWindows = codigoWindows;
        }

        public int CodigoWindows { get; }
    }
}
