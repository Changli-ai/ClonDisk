namespace Cloner.Core.PartitionTables;

/// <summary>
/// Código de arranque propio de ClonDisk para una tabla MBR nueva (conversión desde GPT, RF-17).
/// La BIOS lo carga en 0000:7C00; se copia a 0000:0600, busca la partición activa, lee su primer
/// sector con INT 13h extendida (AH = 42h) en 0000:7C00 y salta a él con DL = unidad y DS:SI
/// apuntando a la entrada de la partición, como esperan los sectores de arranque. Si no hay
/// partición activa o falla la lectura, devuelve el control a la BIOS (INT 18h).
/// </summary>
internal static class MbrBootCode
{
    /// <summary>Bytes que ocupa el código en el sector 0, antes de la firma del disco.</summary>
    public const int Length = 440;

    private static readonly byte[] Program =
    [
        // 0x00: preparar segmentos y pila, y copiarse de 7C00 a 0600.
        0xFA,                   // cli
        0x31, 0xC0,             // xor ax, ax
        0x8E, 0xD0,             // mov ss, ax
        0xBC, 0x00, 0x7C,       // mov sp, 7C00h
        0x8E, 0xD8,             // mov ds, ax
        0x8E, 0xC0,             // mov es, ax
        0xFB,                   // sti
        0xFC,                   // cld
        0xBE, 0x00, 0x7C,       // mov si, 7C00h
        0xBF, 0x00, 0x06,       // mov di, 0600h
        0xB9, 0x00, 0x01,       // mov cx, 256
        0xF3, 0xA5,             // rep movsw
        0xEA, 0x1E, 0x06, 0x00, 0x00, // jmp 0000:061E

        // 0x1E: buscar la entrada activa (0x80) entre las cuatro de la tabla (0600h + 1BEh).
        0xBE, 0xBE, 0x07,       // mov si, 07BEh
        0xB9, 0x04, 0x00,       // mov cx, 4
        0x80, 0x3C, 0x80,       // 0x24: cmp byte [si], 80h
        0x74, 0x0E,             // je 0x37
        0x83, 0xC6, 0x10,       // add si, 16
        0xE2, 0xF6,             // loop 0x24

        // 0x2E: sin partición activa o error de lectura: que la BIOS pruebe otro dispositivo.
        0xCD, 0x18,             // int 18h
        0xEB, 0xFE,             // jmp $
        0x90, 0x90, 0x90, 0x90, 0x90,

        // 0x37: leer el primer sector de la partición con un paquete de direcciones en la pila.
        0x89, 0xF5,             // mov bp, si
        0x66, 0x6A, 0x00,       // push dword 0          ; LBA, parte alta
        0x66, 0xFF, 0x74, 0x08, // push dword [si+8]     ; LBA de inicio de la partición
        0x6A, 0x00,             // push 0                ; segmento del búfer
        0x68, 0x00, 0x7C,       // push 7C00h            ; desplazamiento del búfer
        0x6A, 0x01,             // push 1                ; sectores
        0x6A, 0x10,             // push 10h              ; tamaño del paquete
        0x89, 0xE6,             // mov si, sp
        0xB4, 0x42,             // mov ah, 42h
        0xCD, 0x13,             // int 13h (DL = unidad que dio la BIOS)
        0x72, 0xDD,             // jc 0x2E
        0x83, 0xC4, 0x10,       // add sp, 16
        0x81, 0x3E, 0xFE, 0x7D, 0x55, 0xAA, // cmp word [7DFEh], AA55h
        0x75, 0xD2,             // jne 0x2E
        0x89, 0xEE,             // mov si, bp            ; DS:SI = entrada de la partición
        0xEA, 0x00, 0x7C, 0x00, 0x00, // jmp 0000:7C00
    ];

    /// <summary>Los 440 bytes del código, con el resto a cero.</summary>
    public static byte[] Create()
    {
        var code = new byte[Length];
        Program.CopyTo(code, 0);
        return code;
    }
}
