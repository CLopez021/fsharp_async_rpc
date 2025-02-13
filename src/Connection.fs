module Async_rpc.Connection

open Core_kernel
open System.Collections.Generic
open Async_rpc.Protocol
open Async_rpc.Bin_prot_generated_types
open Core_kernel.Bin_prot_generated_types
open System.Threading.Tasks

type t' =
  { mutable last_seen_alive : System.DateTime
    mutable open_state : Transport.Open_state.t
    writer : Transport.Writer.t
    open_queries : Dictionary<Query.Id.t, Response_handler.t>
    time_source : Time_source.t
    implementations : Dictionary<(Rpc_tag.t * int64), unit Implementation.t> }

type t = T of t' Sequencer.t

let update_last_seen_alive (T t) =
  Sequencer.with_ t (fun t -> t.last_seen_alive <- t.time_source.now ())

let writer t =
  match t.open_state with
  | Transport.Open_state.Open -> Ok t.writer
  | Transport.Open_state.Close_started reason -> Error reason

let expect_version version expected =
  if version = expected then
    Ok()
  else
    Or_error.Error.format
      "Negotiated unexpected version: %d. Expected version: %d"
      version
      expected

let rpc_handshake transport =
  Result.let_syntax {
    let! version =
      Protocol_version_header.handshake_and_negotiate_version
        (Protocol_version_header.v1 Known_protocol.Rpc)
        transport

    return! expect_version version 1L
  }

let do_handshake (transport : Transport.t) protocol =
  Result.let_syntax {
    match protocol with
    | Known_protocol.Rpc -> do! rpc_handshake transport
    | Known_protocol.Krb krb_handshake ->
      do! krb_handshake transport
      do! rpc_handshake transport
    | Known_protocol.Krb_test_mode ->
      let! version =
        Protocol_version_header.handshake_and_negotiate_version
          (Protocol_version_header.v1 Known_protocol.Krb_test_mode)
          transport

      do! expect_version version 1L

      let syn = Krb.Principal.Name.Stable.V1.t.User System.Environment.UserName

      do!
        Transport.Writer.send_bin_prot
          transport.writer
          Krb.Test_mode_protocol.Syn.bin_writer_t
          syn
        |> Transport.Send_result.to_or_error

      let! syn_result =
        Transport.Reader.read_one_message_bin_prot
          transport.reader
          Krb.Test_mode_protocol.Syn.bin_reader_t
        |> Result.mapError (Transport.Reader.Error.to_error)

      (* [lib/krb]'s [Test_mode_protocol] uses an explicit [Krb.Authorize.t] passed
      into the client to verify [syn_result]. Instead of that, we require the server to be
      run as the current user. This fine since the purpose of [Krb_test_mode] in F# is to
      run integration tests against servers running in this test mode. *)
      let ack =
        (if syn = syn_result then
           Ok()
         else
           (Or_error.Error.format
             "We expect the server to be run by the current user in test mode [client:%A] [server:%A]"
             syn
             syn_result))

      do!
        Transport.Writer.send_bin_prot
          transport.writer
          Krb.Test_mode_protocol.Ack.bin_writer_t
          ack
        |> Transport.Send_result.to_or_error

      do!
        Transport.Reader.read_one_message_bin_prot
          transport.reader
          Krb.Test_mode_protocol.Ack.bin_reader_t
        |> Result.mapError (Transport.Reader.Error.to_error)
        |> Result.join

      do! rpc_handshake transport
  }

let default_handshake_timeout_ms = 3000

let with_set_read_timeout (stream : System.IO.Stream) f =
  let original_timeout = stream.ReadTimeout
  stream.ReadTimeout <- default_handshake_timeout_ms

  try
    f ()
  finally
    stream.ReadTimeout <- original_timeout

let do_handshake_with_timeout transport protocol =
  with_set_read_timeout (Transport.stream transport) (fun () ->
    do_handshake transport protocol)

let remove_response_handler t response_id =
  // we do not care if the removal was successful or not, as it's always conservative to
  // remove a handler even if it doesn't exist.
  let (_success : bool) = t.open_queries.Remove response_id
  ()

let dispatch (T t) response_handler bin_writer_query (query : 'a Query.t) =
  Sequencer.with_ t (fun t ->
    match writer t with
    | Error close_reason -> Transport.Send_result.Close_started close_reason
    | Ok writer ->
      Option.iter
        (fun response_handler ->
          remove_response_handler t query.id
          t.open_queries.Add(query.id, response_handler))
        response_handler

      Transport.Writer.send_bin_prot
        writer
        (Message.bin_writer_needs_length (Writer_with_length.of_writer bin_writer_query))
        (Message.t.Query query))
  |> Transport.Send_result.to_or_error

let handle_response (T t) (response : _ Response.t) read_buffer read_buffer_pos_ref =
  Sequencer.with_ t (fun t ->
    match Dictionary.find t.open_queries response.id with
    | None -> Transport.Handler_result.Stop(Rpc_error.t.Unknown_query_id response.id)
    | Some (response_handler : Response_handler.t) ->
      // To avoid blocking the reading thread, a read should trigger an enqueue onto some
      // concurrent queue. However here the response handler callback needs to be called
      // synchronously, because that determines if the response handler is kept or removed.
      // In practice, because this is a private function, it will be the responsibility of
      // the next level up (the RPC API) to fill the concurrent queues. This matches what
      // OCaml does -- [response_handler] is synchronous but the callback fills an Ivar
      // which our concurrent queue is a substitute for.
      (match response_handler response read_buffer read_buffer_pos_ref with
       | Response_handler.Result.Keep -> Transport.Handler_result.Continue
       | Response_handler.Result.Remove removal_circumstances ->
         remove_response_handler t response.id

         (match removal_circumstances with
          | Ok () -> Transport.Handler_result.Continue
          | Error e ->
            // This error logic is almost the same as OCaml, except unless the error
            // is an [Unimplemented_rpc]. In this case we are not implementing any RPCs at
            // all, so it should be fine to stop the reader loop here.
            Transport.Handler_result.Stop e)))

let handle_msg (T t) msg read_buffer read_buffer_pos_ref : _ Transport.Handler_result.t =
  match msg with
  | Message.t.Heartbeat -> Transport.Handler_result.Continue
  | Message.t.Response response ->
    handle_response (T t) response read_buffer read_buffer_pos_ref

  | Message.t.Query query ->
    // In OCaml this raises because there are no implementations and the default
    // behaviour is to throw an exception that gets consumed by an error stream
    // iter that cleans everything up. Here we bubble the error up the call stack
    // so it's handled explicitly.
    let tag = query.tag
    let version = query.version

    Sequencer.with_ t (fun t ->
      match Dictionary.find t.implementations (tag, version) with
      | None ->
        Transport.Handler_result.Stop(
          Rpc_error.t.Unimplemented_rpc(
            query.tag,
            Rpc_error.Unimplemented_rpc.t.Version query.id
          )
        )
      | Some foo ->

        let result =
          Implementation.apply foo () query read_buffer read_buffer_pos_ref t.writer

        match result with
        | Ok () -> Transport.Handler_result.Continue
        | Error error -> Transport.Handler_result.Stop error)



let on_message t message =
  let buf = new Bin_prot.Buffer.Buffer<byte>(message : byte [])
  let pos_ref = ref 0
  let nat0_msg = Message.bin_reader_nat0_t.read buf pos_ref

  match handle_msg t nat0_msg buf pos_ref with
  | Transport.Handler_result.Continue -> Transport.Handler_result.Continue
  | Transport.Handler_result.Stop result ->
    Transport.Handler_result.Stop(sprintf "Rpc message handling loop stopped: %A" result)

let cleanup t reason =
  match t.open_state with
  | Transport.Open_state.Close_started (_ : Transport.Close_reason.t) -> ()
  | Transport.Open_state.Open ->
    t.open_state <- Transport.Open_state.Close_started reason
    Transport.Writer.close t.writer

let close (T t) =
  Sequencer.with_ t (fun t -> cleanup t (Transport.Close_reason.By_user))

let open_state (T t) = Sequencer.with_ t (fun t -> t.open_state)

let heartbeat_timeout = System.TimeSpan.FromSeconds 30.
let send_heartbeat_every = System.TimeSpan.FromSeconds 10.

let heartbeat_now (T t) =
  Sequencer.with_ t (fun t ->
    let since_last_heartbeat = t.time_source.now () - t.last_seen_alive

    if since_last_heartbeat > heartbeat_timeout then
      Or_error.Error.format "No heartbeats received for %A." heartbeat_timeout
    else
      (match writer t with
       | Error close_reason -> Transport.Send_result.Close_started close_reason
       | Ok writer ->
         Transport.Writer.send_bin_prot
           writer
           Message.bin_writer_nat0_t
           Message.t.Heartbeat)
      |> Transport.Send_result.to_or_error)

let heartbeat_periodically (T t) =
  update_last_seen_alive (T t)

  let rec loop_until_error () =
    match heartbeat_now (T t) with
    | Ok () ->
      // Though this access breaks the lock, [Time_source] is thread-safe and this
      // access doesn't break any transaction promises we make.
      (Sequencer.with_ t (fun t -> t.time_source))
        .sleep_for send_heartbeat_every

      loop_until_error ()
    | Error e -> e

  let e = loop_until_error ()

  Sequencer.with_ t (fun t ->
    cleanup t (Transport.Close_reason.errorf "Heartbeat thread stopped: %A" e))

let close_outstanding_queries open_queries =
  let dummy_buffer = Bin_prot.Buffer.Buffer(1)
  let dummy_ref = ref 0

  Seq.iter
    (fun (entry : KeyValuePair<_, _>) ->
      let query_id, (response_handler : Response_handler.t) = entry.Key, entry.Value

      response_handler
        ({ id = query_id
           data = Error Rpc_error.t.Connection_closed } : _ Response.t)
        dummy_buffer
        dummy_ref
      |> (ignore : Response_handler.Result.t -> unit))
    open_queries

let run_after_handshake (T t) reader =
  let result =
    Transport.Reader.read_forever reader (on_message (T t)) (fun () ->
      update_last_seen_alive (T t))

  let reason = Transport.Close_reason.errorf "Connection reader loop finished: %A" result

  Sequencer.with_ t (fun t ->
    cleanup t reason
    // In OCaml, this is part of the cleanup function. Here it's after the reader loop finishes,
    // so that handlers are always called in the reader thread.
    close_outstanding_queries t.open_queries)


let create
  stream
  (time_source : Time_source.t)
  protocol
  (args : {| max_message_size : int |})
  f
  (implementations : unit Implementation.t list)
  =
  let implementation_dict = new Dictionary<Rpc_tag.t * int64, unit Implementation.t>()

  implementations
  |> List.iter (fun implementation ->
    implementation_dict.Add(Implementation.rpc_info implementation, implementation))

  let create_and_handshake () =
    Result.let_syntax {
      let! transport = Transport.create stream args

      let t =
        { writer = transport.writer
          open_state = Transport.Open_state.Open
          last_seen_alive = time_source.now ()
          open_queries = Dictionary()
          time_source = time_source
          implementations = implementation_dict }
        |> Sequencer.create

      Transport.Writer.set_close_finished_callback transport.writer (fun close_reason ->
        Sequencer.with_ t (fun t ->
          cleanup t (Transport.Close_reason.errorf "Writer stopped: %A" close_reason)))

      match do_handshake_with_timeout transport protocol with
      | Ok () -> return (T t, transport.reader)
      | Error error ->
        Sequencer.with_ t (fun t ->
          cleanup t (Transport.Close_reason.errorf "Handshake failed"))

        return! Or_error.Error.format "Handshake error: %A" error
    }

  Thread.spawn_and_ignore "connect then heartbeat loop" (fun () ->
    match create_and_handshake () with
    | Ok (t, reader) ->
      Thread.spawn_and_ignore "reader loop" (fun () -> run_after_handshake t reader)
      f (Ok t)
      heartbeat_periodically t
    | Error error -> f (Error error))

module For_testing =
  let create_wait_for_connection
    stream
    time_source
    protocol
    (args : {| max_message_size : int |})
    =
    let wait_connection = TaskCompletionSource<_>()

    create
      stream
      time_source
      protocol
      {| max_message_size = args.max_message_size |}
      wait_connection.SetResult
      []

    wait_connection.Task.Result

  let open_queries (T t) =
    Sequencer.with_ t (fun t -> t.open_queries.Keys)
    |> List.ofSeq

  let send_heartbeat_every = send_heartbeat_every
  let heartbeat_timeout = heartbeat_timeout
