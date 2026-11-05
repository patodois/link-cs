# Leitura de pixels e tolerância a truncamento

O formato no fio e os offsets do arquivo da revisão 6 permanecem iguais.

No Unix o leitor não mapeia páginas do peer. Usa I/O posicional do descritor aberto,
com leitura limitada do cabeçalho, validação dos tamanhos, cópia dos planos e segunda
observação do lock. Uma leitura curta é completada; EOF ou erro descarta a cópia.
A consulta de tamanho é uma rejeição antecipada, não uma proteção do mapa.
Linhas contíguas são copiadas em bloco. Java 8 usa scratch direto de 1 MiB e
FileChannel.read com posição. C# netstandard2.0 usa pread de libc com SafeFileHandle
e buffers fixados, evitando cache de FileStream e preservando o tempo de vida do handle.

No Windows o mapa somente leitura permanece, com as cargas atômicas anteriores.
SetEndOfFile normalmente recusa truncar um arquivo com seção ativa, mesmo se outro
processo tenta fazê-lo. Rust e os ports gerenciados leem o cabeçalho por I/O limitado antes do mapa;
o C só acessa seu mapa depois de confirmar novamente o tamanho com a seção já ativa.
Os quatro recusam um encolhimento anterior à criação do mapa antes de usá-lo. Windows
não foi rodado nesta tarefa. Falhas de paginação e de armazenamento continuam fora
da garantia. Linux também não foi rodado; o caminho POSIX foi executado no macOS arm64.

Leituras de apresentação avançam o contador apenas após cópia válida. Uma nova instância
de leitor inicia a ordem em zero. Um consumidor deve limpar a imagem e descartar resultados
assíncronos do leitor anterior ao receber frame_channel ou perder a sessão, inclusive se
abrir o arquivo novo falha. Leituras brutas não filtram ordem.

A revisão 6 diz literalmente mmap e cargas acquire; pread elimina a falha de página por
truncamento, mas POSIX não define uma leitura como carga atômica acquire do escritor.
As barreiras, comparações de lock e testes de contenção dão evidência local, sem uma prova
para todo SO e sistema de arquivos. Um escritor que reutilize lock e frame ao reconstruir
conteúdo no mesmo arquivo pode causar ABA. O writer continua mapeado e depende de que
ninguém trunque seu próprio arquivo. I/O pode bloquear. No Rust, WASI continua sem suporte
a pixels porque o writer precisa de mmap. Uma negociação futura de handles com tamanho
protegido e identificador de canal exigiria extensão do contrato.

Benchmarks: arquivos sintéticos com cache aquecido, duas leituras de aquecimento,
30 amostras nas resoluções menores e 5 nos frames de 2^26 pixels; cor e cor com depth.
São medidas CPU de cópia completa, com validação, e não incluem GPU, IPC ou renderização.
Rust e C reutilizam buffers; C# e Java alocam a saída conforme suas APIs. GC é solicitado
antes das amostras gerenciadas, fora da região medida. Não comparar os números entre
linguagens como se as políticas de alocação fossem iguais. O arquivo é removido após cada caso.

## Medição local desta implementação

macOS arm64, valores em ms por frame. mmap anterior versus I/O posicional final.

| Tamanho | Depth | mmap p50 | I/O p50 | I/O p95 |
|---|---|---:|---:|---:|
| 1280x720 | não | 0.2089 | 0.1897 | 0.2185 |
| 1280x720 | sim | 0.3262 | 0.3284 | 0.3905 |
| 1920x1080 | não | 0.2968 | 0.2623 | 0.3970 |
| 1920x1080 | sim | 0.8491 | 0.9318 | 1.2000 |
| 3840x2160 | não | 1.9132 | 2.2740 | 2.7723 |
| 3840x2160 | sim | 4.0477 | 5.1168 | 5.5891 |
| 8192x8192 | não | 15.6192 | 21.9520 | 22.3632 |
| 8192x8192 | sim | 36.8464 | 45.8610 | 46.2605 |
| 16384x1 | não | 0.0025 | 0.0064 | 0.0127 |
| 16384x1 | sim | 0.0037 | 0.0078 | 0.0105 |
| 16384x4096 | não | 17.2385 | 20.9002 | 21.5360 |
| 16384x4096 | sim | 32.9470 | 43.2858 | 50.3929 |

Em Windows, o leitor Java tenta liberar o mapa ao fechar pelo Cleaner já usado pelo writer.
Se a JVM não permitir esse acesso, a liberação depende do GC, podendo atrasar a remoção do arquivo.
Esse caminho não foi rodado. Mono, Unity e IL2CPP também não foram rodados.
