import sys, struct
# HuC6280 opcode table: (mnemonic, addressing mode)
T={}
def s(op,m,mode): T[op]=(m,mode)
base=[
(0x00,'BRK','imp'),(0x01,'ORA','inx'),(0x02,'SXY','imp'),(0x03,'ST0','imm'),(0x04,'TSB','zp'),(0x05,'ORA','zp'),(0x06,'ASL','zp'),(0x07,'RMB0','zp'),(0x08,'PHP','imp'),(0x09,'ORA','imm'),(0x0A,'ASL','acc'),(0x0C,'TSB','abs'),(0x0D,'ORA','abs'),(0x0E,'ASL','abs'),(0x0F,'BBR0','zprel'),
(0x10,'BPL','rel'),(0x11,'ORA','iny'),(0x12,'ORA','zpi'),(0x13,'ST1','imm'),(0x14,'TRB','zp'),(0x15,'ORA','zpx'),(0x16,'ASL','zpx'),(0x17,'RMB1','zp'),(0x18,'CLC','imp'),(0x19,'ORA','aby'),(0x1A,'INC','acc'),(0x1C,'TRB','abs'),(0x1D,'ORA','abx'),(0x1E,'ASL','abx'),(0x1F,'BBR1','zprel'),
(0x20,'JSR','abs'),(0x21,'AND','inx'),(0x22,'SAX','imp'),(0x23,'ST2','imm'),(0x24,'BIT','zp'),(0x25,'AND','zp'),(0x26,'ROL','zp'),(0x27,'RMB2','zp'),(0x28,'PLP','imp'),(0x29,'AND','imm'),(0x2A,'ROL','acc'),(0x2C,'BIT','abs'),(0x2D,'AND','abs'),(0x2E,'ROL','abs'),(0x2F,'BBR2','zprel'),
(0x30,'BMI','rel'),(0x31,'AND','iny'),(0x32,'AND','zpi'),(0x34,'BIT','zpx'),(0x35,'AND','zpx'),(0x36,'ROL','zpx'),(0x37,'RMB3','zp'),(0x38,'SEC','imp'),(0x39,'AND','aby'),(0x3A,'DEC','acc'),(0x3C,'BIT','abx'),(0x3D,'AND','abx'),(0x3E,'ROL','abx'),(0x3F,'BBR3','zprel'),
(0x40,'RTI','imp'),(0x41,'EOR','inx'),(0x42,'SAY','imp'),(0x43,'TMA','imm'),(0x44,'BSR','rel'),(0x45,'EOR','zp'),(0x46,'LSR','zp'),(0x47,'RMB4','zp'),(0x48,'PHA','imp'),(0x49,'EOR','imm'),(0x4A,'LSR','acc'),(0x4C,'JMP','abs'),(0x4D,'EOR','abs'),(0x4E,'LSR','abs'),(0x4F,'BBR4','zprel'),
(0x50,'BVC','rel'),(0x51,'EOR','iny'),(0x52,'EOR','zpi'),(0x53,'TAM','imm'),(0x54,'CSL','imp'),(0x55,'EOR','zpx'),(0x56,'LSR','zpx'),(0x57,'RMB5','zp'),(0x58,'CLI','imp'),(0x59,'EOR','aby'),(0x5A,'PHY','imp'),(0x5D,'EOR','abx'),(0x5E,'LSR','abx'),(0x5F,'BBR5','zprel'),
(0x60,'RTS','imp'),(0x61,'ADC','inx'),(0x62,'CLA','imp'),(0x64,'STZ','zp'),(0x65,'ADC','zp'),(0x66,'ROR','zp'),(0x67,'RMB6','zp'),(0x68,'PLA','imp'),(0x69,'ADC','imm'),(0x6A,'ROR','acc'),(0x6C,'JMP','ind'),(0x6D,'ADC','abs'),(0x6E,'ROR','abs'),(0x6F,'BBR6','zprel'),
(0x70,'BVS','rel'),(0x71,'ADC','iny'),(0x72,'ADC','zpi'),(0x73,'TII','blk'),(0x74,'STZ','zpx'),(0x75,'ADC','zpx'),(0x76,'ROR','zpx'),(0x77,'RMB7','zp'),(0x78,'SEI','imp'),(0x79,'ADC','aby'),(0x7A,'PLY','imp'),(0x7C,'JMP','aix'),(0x7D,'ADC','abx'),(0x7E,'ROR','abx'),(0x7F,'BBR7','zprel'),
(0x80,'BRA','rel'),(0x81,'STA','inx'),(0x82,'CLX','imp'),(0x83,'TST','imzp'),(0x84,'STY','zp'),(0x85,'STA','zp'),(0x86,'STX','zp'),(0x87,'SMB0','zp'),(0x88,'DEY','imp'),(0x89,'BIT','imm'),(0x8A,'TXA','imp'),(0x8C,'STY','abs'),(0x8D,'STA','abs'),(0x8E,'STX','abs'),(0x8F,'BBS0','zprel'),
(0x90,'BCC','rel'),(0x91,'STA','iny'),(0x92,'STA','zpi'),(0x93,'TST','imabs'),(0x94,'STY','zpx'),(0x95,'STA','zpx'),(0x96,'STX','zpy'),(0x97,'SMB1','zp'),(0x98,'TYA','imp'),(0x99,'STA','aby'),(0x9A,'TXS','imp'),(0x9C,'STZ','abs'),(0x9D,'STA','abx'),(0x9E,'STZ','abx'),(0x9F,'BBS1','zprel'),
(0xA0,'LDY','imm'),(0xA1,'LDA','inx'),(0xA2,'LDX','imm'),(0xA3,'TST','imzpx'),(0xA4,'LDY','zp'),(0xA5,'LDA','zp'),(0xA6,'LDX','zp'),(0xA7,'SMB2','zp'),(0xA8,'TAY','imp'),(0xA9,'LDA','imm'),(0xAA,'TAX','imp'),(0xAC,'LDY','abs'),(0xAD,'LDA','abs'),(0xAE,'LDX','abs'),(0xAF,'BBS2','zprel'),
(0xB0,'BCS','rel'),(0xB1,'LDA','iny'),(0xB2,'LDA','zpi'),(0xB3,'TST','imabsx'),(0xB4,'LDY','zpx'),(0xB5,'LDA','zpx'),(0xB6,'LDX','zpy'),(0xB7,'SMB3','zp'),(0xB8,'CLV','imp'),(0xB9,'LDA','aby'),(0xBA,'TSX','imp'),(0xBC,'LDY','abx'),(0xBD,'LDA','abx'),(0xBE,'LDX','aby'),(0xBF,'BBS3','zprel'),
(0xC0,'CPY','imm'),(0xC1,'CMP','inx'),(0xC2,'CLY','imp'),(0xC3,'TDD','blk'),(0xC4,'CPY','zp'),(0xC5,'CMP','zp'),(0xC6,'DEC','zp'),(0xC7,'SMB4','zp'),(0xC8,'INY','imp'),(0xC9,'CMP','imm'),(0xCA,'DEX','imp'),(0xCC,'CPY','abs'),(0xCD,'CMP','abs'),(0xCE,'DEC','abs'),(0xCF,'BBS4','zprel'),
(0xD0,'BNE','rel'),(0xD1,'CMP','iny'),(0xD2,'CMP','zpi'),(0xD3,'TIN','blk'),(0xD4,'CSH','imp'),(0xD5,'CMP','zpx'),(0xD6,'DEC','zpx'),(0xD7,'SMB5','zp'),(0xD8,'CLD','imp'),(0xD9,'CMP','aby'),(0xDA,'PHX','imp'),(0xDD,'CMP','abx'),(0xDE,'DEC','abx'),(0xDF,'BBS5','zprel'),
(0xE0,'CPX','imm'),(0xE1,'SBC','inx'),(0xE3,'TIA','blk'),(0xE4,'CPX','zp'),(0xE5,'SBC','zp'),(0xE6,'INC','zp'),(0xE7,'SMB6','zp'),(0xE8,'INX','imp'),(0xE9,'SBC','imm'),(0xEA,'NOP','imp'),(0xEC,'CPX','abs'),(0xED,'SBC','abs'),(0xEE,'INC','abs'),(0xEF,'BBS6','zprel'),
(0xF0,'BEQ','rel'),(0xF1,'SBC','iny'),(0xF2,'SBC','zpi'),(0xF3,'TAI','blk'),(0xF4,'SET','imp'),(0xF5,'SBC','zpx'),(0xF6,'INC','zpx'),(0xF7,'SMB7','zp'),(0xF8,'SED','imp'),(0xF9,'SBC','aby'),(0xFA,'PLX','imp'),(0xFD,'SBC','abx'),(0xFE,'INC','abx'),(0xFF,'BBS7','zprel'),
]
for o,m,md in base: s(o,m,md)
LEN={'imp':1,'acc':1,'imm':2,'zp':2,'zpx':2,'zpy':2,'zpi':2,'inx':2,'iny':2,'rel':2,'abs':3,'abx':3,'aby':3,'ind':3,'aix':3,'zprel':3,'blk':7,'imzp':3,'imzpx':3,'imabs':4,'imabsx':4}

def fmt(pc, b, md):
    if md in('imp','acc'): return ''
    if md=='imm': return f"#${b[1]:02X}"
    if md=='zp': return f"<${b[1]:02X}"
    if md=='zpx': return f"<${b[1]:02X},X"
    if md=='zpy': return f"<${b[1]:02X},Y"
    if md=='zpi': return f"(<${b[1]:02X})"
    if md=='inx': return f"(<${b[1]:02X},X)"
    if md=='iny': return f"(<${b[1]:02X}),Y"
    if md=='rel': return f"${(pc+2+((b[1]^0x80)-0x80))&0xFFFF:04X}"
    if md=='abs': return f"${b[1]|b[2]<<8:04X}"
    if md=='abx': return f"${b[1]|b[2]<<8:04X},X"
    if md=='aby': return f"${b[1]|b[2]<<8:04X},Y"
    if md=='ind': return f"(${b[1]|b[2]<<8:04X})"
    if md=='aix': return f"(${b[1]|b[2]<<8:04X},X)"
    if md=='zprel': return f"<${b[1]:02X},${(pc+3+((b[2]^0x80)-0x80))&0xFFFF:04X}"
    if md=='blk': return f"${b[1]|b[2]<<8:04X},${b[3]|b[4]<<8:04X},${b[5]|b[6]<<8:04X}"
    if md=='imzp': return f"#${b[1]:02X},<${b[2]:02X}"
    if md=='imzpx': return f"#${b[1]:02X},<${b[2]:02X},X"
    if md=='imabs': return f"#${b[1]:02X},${b[2]|b[3]<<8:04X}"
    if md=='imabsx': return f"#${b[1]:02X},${b[2]|b[3]<<8:04X},X"

def dis(rom, start_off, cpu_base, length, out=sys.stdout):
    i=start_off; end=start_off+length
    while i<end:
        op=rom[i]; pc=cpu_base+(i-start_off)
        if op not in T:
            out.write(f"{i:05X} {pc:04X}: {op:02X}        .db ${op:02X}\n"); i+=1; continue
        m,md=T[op]; n=LEN[md]; b=rom[i:i+n]
        hx=' '.join(f"{x:02X}" for x in b).ljust(20)
        out.write(f"{i:05X} {pc:04X}: {hx} {m} {fmt(pc,b,md)}\n")
        i+=n

if __name__=='__main__':
    rom=open(sys.argv[1],'rb').read()
    off=int(sys.argv[2],16); base=int(sys.argv[3],16); ln=int(sys.argv[4],16)
    dis(rom,off,base,ln)
